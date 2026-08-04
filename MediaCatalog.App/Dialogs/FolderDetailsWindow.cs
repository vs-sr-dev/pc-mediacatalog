using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using MediaCatalog.App.ViewModels;

namespace MediaCatalog.App;

/// <summary>
/// Everything a whole folder can be told at once: what the programme or film in it is
/// called, what year it is from, and what sort of thing it is.
///
/// This exists because correcting one of those a file at a time is not a reasonable thing to
/// ask. A series whose file names carry the year of each season rather than of the show ends
/// up with twelve episodes filed under a year that is nobody's idea of right, and the fix is
/// one number typed once — not twelve trips through the details dialog.
///
/// A field left as it was is not written at all, so correcting the year does not quietly
/// re-stamp the title as hand-typed. The year has its own tick-box because "leave it alone"
/// and "it has no year" are different instructions and an empty box cannot say both.
/// </summary>
public class FolderDetailsWindow : Window
{
    private readonly ComboBox _folder = new();
    private readonly TextBox _title = new();
    private readonly TextBox _year = new();
    private readonly CheckBox _setYear = new()
    {
        Content = "Set the year", VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 10, 0)
    };
    private readonly ComboBox _category = new() { IsEditable = true };
    private readonly CheckBox _setCategory = new()
    {
        Content = "Set the category", VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 10, 0)
    };
    private readonly CheckBox _subdirs = new()
    {
        Content = "Include all subfolders", IsChecked = true, Margin = new Thickness(0, 10, 0, 0)
    };
    private readonly TextBlock _summary = new()
    {
        Foreground = System.Windows.Media.Brushes.Gray, TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 2, 0, 10)
    };

    private readonly Func<string, MainViewModel.FolderDetails> _lookup;
    private readonly string _fallbackTitle;

    public string SelectedFolder => _folder.SelectedItem as string ?? _folder.Text;
    public bool IncludeSubdirectories => _subdirs.IsChecked == true;

    /// <summary>What the user asked for, with anything untouched left null.</summary>
    public MainViewModel.FolderDetails Details { get; private set; } =
        new(null, null, false, null);

    /// <param name="lookup">
    /// What a folder's files already agree on, so the boxes open showing the truth rather
    /// than empty. Called again whenever the folder is changed, since picking the parent of
    /// a season folder is picking a different set of files.
    /// </param>
    /// <param name="fallbackTitle">
    /// The folder's own name, offered when its files do not agree on a title — which is
    /// usually the show's name and always a better starting point than nothing.
    /// </param>
    public FolderDetailsWindow(
        string folder,
        IReadOnlyList<string> categories,
        Func<string, MainViewModel.FolderDetails> lookup,
        string fallbackTitle)
    {
        _lookup = lookup;
        _fallbackTitle = fallbackTitle;

        Title = "Set folder details"; Width = 600; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ResizeMode = ResizeMode.NoResize;

        var panel = new StackPanel { Margin = new Thickness(14) };

        // The folder and its ancestors, so the correction can be made higher up the tree —
        // on the show rather than on one season of it.
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
        _folder.Margin = new Thickness(0, 2, 0, 4);
        _folder.SelectionChanged += (_, _) => LoadFolder();
        panel.Children.Add(_folder);
        panel.Children.Add(_summary);

        panel.Children.Add(new TextBlock { Text = "Title:" });
        _title.Margin = new Thickness(0, 2, 0, 10);
        panel.Children.Add(_title);

        panel.Children.Add(Row(_setYear, "Year:", _year,
            "Tick this to write the year. Leave the box empty with it ticked to take the year " +
            "off — \"leave it alone\" and \"it has none\" are different instructions."));

        _category.ItemsSource = categories;
        panel.Children.Add(Row(_setCategory, "Category:", _category,
            "Season and episode numbers belong to TvShow and TvExtra alone; filing these as " +
            "anything else clears them."));

        panel.Children.Add(_subdirs);
        panel.Children.Add(new TextBlock
        {
            Text = "Counts as confirmed, so these files can be consolidated. Anything you leave " +
                   "untouched here is not written at all — correcting the year does not restamp " +
                   "the title.",
            Foreground = System.Windows.Media.Brushes.Gray, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0)
        });

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0)
        };
        var ok = new Button
        {
            Content = "OK", Width = 78, IsDefault = true, Margin = new Thickness(0, 0, 6, 0)
        };
        ok.Click += (_, _) => Commit();
        buttons.Children.Add(ok);
        buttons.Children.Add(new Button { Content = "Cancel", Width = 78, IsCancel = true });
        panel.Children.Add(buttons);

        Content = panel;
        LoadFolder();
        Loaded += (_, _) => { _title.Focus(); _title.SelectAll(); };
    }

    /// <summary>A tick-box, a label and the control it enables, on one line.</summary>
    private static FrameworkElement Row(CheckBox toggle, string label, Control control, string hint)
    {
        control.IsEnabled = false;
        control.Width = control is TextBox ? 90 : 160;
        control.VerticalContentAlignment = VerticalAlignment.Center;
        toggle.Checked += (_, _) => control.IsEnabled = true;
        toggle.Unchecked += (_, _) => control.IsEnabled = false;

        var line = new StackPanel { Orientation = Orientation.Horizontal };
        line.Children.Add(toggle);
        line.Children.Add(new TextBlock
        {
            Text = label, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        });
        line.Children.Add(control);

        var wrap = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        wrap.Children.Add(line);
        wrap.Children.Add(new TextBlock
        {
            Text = hint, Foreground = System.Windows.Media.Brushes.Gray,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0)
        });
        return wrap;
    }

    /// <summary>Show what the chosen folder's files already say about themselves.</summary>
    private void LoadFolder()
    {
        var folder = SelectedFolder;
        if (string.IsNullOrWhiteSpace(folder)) return;

        var current = _lookup(folder);

        _title.Text = current.Title ?? _fallbackTitle;
        _year.Text = current.Year?.ToString() ?? string.Empty;
        _category.Text = current.Category ?? string.Empty;

        var disagreements = new List<string>();
        if (current.Title == null) disagreements.Add("title");
        if (current.Year == null) disagreements.Add("year");
        if (current.Category == null) disagreements.Add("category");

        _summary.Text = disagreements.Count == 0
            ? "Its files agree on all of these."
            : "Its files do not agree on: " + string.Join(", ", disagreements) +
              " — whatever you set here is written to all of them.";
    }

    private void Commit()
    {
        int? year = null;
        if (_setYear.IsChecked == true && _year.Text.Trim().Length > 0)
        {
            if (!int.TryParse(_year.Text.Trim(), out var parsed) || parsed is < 1800 or > 2200)
            {
                MessageBox.Show(this, "Write the year as four digits, or clear the box to take " +
                                      "the year off altogether.",
                    "Set folder details", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            year = parsed;
        }

        var title = _title.Text.Trim();
        var category = _setCategory.IsChecked == true ? _category.Text.Trim() : string.Empty;

        Details = new MainViewModel.FolderDetails(
            title.Length > 0 ? title : null,
            year,
            _setYear.IsChecked == true,
            category.Length > 0 ? category : null);

        if (Details is { Title: null, ChangeYear: false, Category: null })
        {
            MessageBox.Show(this, "Nothing has been set, so there is nothing to apply.",
                "Set folder details", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        DialogResult = true;
    }
}
