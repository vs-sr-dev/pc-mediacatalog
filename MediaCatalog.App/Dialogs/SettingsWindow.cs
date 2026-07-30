using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using MediaCatalog.Core.Storage;
using Microsoft.Win32;

namespace MediaCatalog.App;

/// <summary>
/// Edits <see cref="AppSettings"/>: TMDb key, consolidation targets, ignored file types,
/// excluded folders and custom categories. Folder category rules are preserved untouched.
/// </summary>
public class SettingsWindow : Window
{
    private sealed class ExclRow
    {
        public required ExcludedFolder Model { get; init; }
        public string Display => Model.Path + (Model.IncludeSubdirectories ? "   [+ subfolders]" : "   [this folder only]");
    }

    private readonly AppSettings _incoming;
    private readonly TextBox _apiKey = new();
    private readonly TextBox _tvDir = new();
    private readonly TextBox _filmDir = new();
    private readonly ObservableCollection<string> _exts = new();
    private readonly ObservableCollection<ExclRow> _excluded = new();
    private readonly ObservableCollection<string> _categories = new();

    public AppSettings Result { get; private set; }

    public SettingsWindow(AppSettings settings, IReadOnlyList<string> knownCategories)
    {
        _incoming = settings;
        Result = settings;

        Title = "Settings"; Width = 660; Height = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _apiKey.Text = settings.TmdbApiKey;
        _tvDir.Text = settings.TvConsolidationDir;
        _filmDir.Text = settings.FilmConsolidationDir;
        foreach (var e in settings.IgnoredExtensions) _exts.Add(e);
        foreach (var f in settings.ExcludedFolders) _excluded.Add(new ExclRow { Model = f });
        foreach (var c in settings.CustomCategories) _categories.Add(c);

        var root = new DockPanel { Margin = new Thickness(14) };

        // Buttons pinned to the bottom.
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0)
        };
        var save = new Button { Content = "Save", Width = 84, IsDefault = true, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 6, 0) };
        save.Click += OnSave;
        buttons.Children.Add(save);
        buttons.Children.Add(new Button { Content = "Cancel", Width = 84, IsCancel = true });
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        var panel = new StackPanel();
        panel.Children.Add(Section("themoviedb.org"));
        panel.Children.Add(Labeled("API key (v3):", _apiKey));
        panel.Children.Add(Hint("Get a free key at themoviedb.org → account settings → API. Queries are rate-limited to one every two seconds."));

        panel.Children.Add(Section("Consolidation folders"));
        panel.Children.Add(DirRow("TV shows:", _tvDir));
        panel.Children.Add(DirRow("Films:", _filmDir));

        panel.Children.Add(Section("Ignored file types (removed from results and skipped in future scans)"));
        panel.Children.Add(ListEditor(_exts, addPrompt: "Extension e.g. .nfo", onAdd: AddExtension));

        panel.Children.Add(Section("Excluded folders"));
        panel.Children.Add(ExcludedEditor());

        panel.Children.Add(Section("Custom categories"));
        panel.Children.Add(ListEditor(_categories, addPrompt: "New category name", onAdd: AddCategory));

        root.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = panel
        });
        Content = root;
    }

    // --- Layout helpers ---------------------------------------------------

    private static TextBlock Section(string text) => new()
    {
        Text = text, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 12, 0, 4)
    };

    private static TextBlock Hint(string text) => new()
    {
        Text = text, Foreground = System.Windows.Media.Brushes.Gray,
        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0)
    };

    private static FrameworkElement Labeled(string label, FrameworkElement control)
    {
        var dp = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
        var t = new TextBlock { Text = label, Width = 90, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(t, Dock.Left);
        dp.Children.Add(t);
        dp.Children.Add(control);
        return dp;
    }

    private FrameworkElement DirRow(string label, TextBox box)
    {
        var dp = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
        var t = new TextBlock { Text = label, Width = 90, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(t, Dock.Left);
        dp.Children.Add(t);
        var browse = new Button { Content = "Browse…", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(6, 0, 0, 0) };
        browse.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog { Title = "Choose folder" };
            if (dlg.ShowDialog(this) == true) box.Text = dlg.FolderName;
        };
        DockPanel.SetDock(browse, Dock.Right);
        dp.Children.Add(browse);
        dp.Children.Add(box);
        return dp;
    }

    private FrameworkElement ListEditor(ObservableCollection<string> items, string addPrompt, System.Action<string> onAdd)
    {
        var dp = new DockPanel();
        var list = new ListBox { Height = 90, ItemsSource = items };

        var controls = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        var input = new TextBox { Width = 260, VerticalContentAlignment = VerticalAlignment.Center };
        controls.Children.Add(input);
        var add = new Button { Content = "Add", Width = 64, Margin = new Thickness(6, 0, 0, 0) };
        add.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(input.Text)) { onAdd(input.Text.Trim()); input.Clear(); } };
        controls.Children.Add(add);
        var remove = new Button { Content = "Remove selected", Width = 120, Margin = new Thickness(6, 0, 0, 0) };
        remove.Click += (_, _) => { if (list.SelectedItem is string s) items.Remove(s); };
        controls.Children.Add(remove);

        var wrap = new StackPanel();
        wrap.Children.Add(list);
        DockPanel.SetDock(controls, Dock.Bottom);
        wrap.Children.Add(controls);
        dp.Children.Add(wrap);
        _ = input; _ = addPrompt;
        input.ToolTip = addPrompt;
        return dp;
    }

    private FrameworkElement ExcludedEditor()
    {
        var wrap = new StackPanel();
        var list = new ListBox { Height = 90, ItemsSource = _excluded, DisplayMemberPath = nameof(ExclRow.Display) };
        wrap.Children.Add(list);

        var controls = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        var subdirs = new CheckBox { Content = "incl. subfolders", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        controls.Children.Add(subdirs);
        var add = new Button { Content = "Add folder…", Width = 100 };
        add.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog { Title = "Choose folder to exclude" };
            if (dlg.ShowDialog(this) == true)
                _excluded.Add(new ExclRow { Model = new ExcludedFolder { Path = dlg.FolderName, IncludeSubdirectories = subdirs.IsChecked == true } });
        };
        controls.Children.Add(add);
        var remove = new Button { Content = "Remove selected", Width = 120, Margin = new Thickness(6, 0, 0, 0) };
        remove.Click += (_, _) => { if (list.SelectedItem is ExclRow r) _excluded.Remove(r); };
        controls.Children.Add(remove);
        wrap.Children.Add(controls);
        return wrap;
    }

    private void AddExtension(string ext)
    {
        var normalized = ext.StartsWith('.') ? ext.ToLowerInvariant() : "." + ext.ToLowerInvariant();
        if (!_exts.Contains(normalized)) _exts.Add(normalized);
    }

    private void AddCategory(string name)
    {
        if (!_categories.Contains(name)) _categories.Add(name);
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        Result = new AppSettings
        {
            TmdbApiKey = _apiKey.Text.Trim(),
            TvConsolidationDir = _tvDir.Text.Trim(),
            FilmConsolidationDir = _filmDir.Text.Trim(),
            IgnoredExtensions = _exts.ToList(),
            ExcludedFolders = _excluded.Select(r => r.Model).ToList(),
            CustomCategories = _categories.ToList(),
            FolderCategoryRules = _incoming.FolderCategoryRules // preserved
        };
        DialogResult = true;
    }
}
