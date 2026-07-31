using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MediaCatalog.Core.Storage;
using Microsoft.Win32;

namespace MediaCatalog.App;

/// <summary>
/// Edits <see cref="AppSettings"/>: TMDb credentials, per-category consolidation folders,
/// ignored file types, excluded folders, watched drives and custom categories. Folder
/// category rules are preserved untouched.
///
/// Shown non-modally so the main window stays usable while it is open: saving raises
/// <see cref="Saved"/> rather than setting a dialog result.
/// </summary>
public class SettingsWindow : Window
{
    private sealed class ExclRow
    {
        public required ExcludedFolder Model { get; init; }
        public string Display => Model.Path +
            (AppSettings.HasWildcard(Model.Path) ? "   [pattern]" : "") +
            (Model.IncludeSubdirectories ? "   [+ subfolders]" : "   [this folder only]");
    }

    /// <summary>One category → folder row, with the controls that edit it.</summary>
    private sealed class CatFolderRow
    {
        public required ComboBox Category { get; init; }
        public required TextBox Folder { get; init; }
        public required FrameworkElement Container { get; init; }
    }

    private readonly AppSettings _incoming;
    private readonly IReadOnlyList<string> _knownCategories;
    private readonly IReadOnlyList<string> _driveRoots;

    private readonly TextBox _apiKey = new();
    private readonly TextBox _readToken = new();
    private readonly CheckBox _startup = new() { Content = "Start Media Catalog when Windows starts" };
    private readonly CheckBox _startInTray = new()
    {
        Content = "…and start hidden in the notification area, without opening the window",
        Margin = new Thickness(20, 2, 0, 2)
    };
    private readonly CheckBox _watch = new() { Content = "Watch for new files and add them (with a taskbar notification)" };
    private readonly CheckBox _rememberFilters = new()
    {
        Content = "Remember the view and filters between sessions"
    };
    private readonly CheckBox _excludeSystem = new()
    {
        Content = "Automatically exclude system directories (Windows, Program Files, $Recycle.Bin, …)"
    };

    private readonly ObservableCollection<string> _exts = new();
    private readonly ObservableCollection<ExclRow> _excluded = new();
    private readonly ObservableCollection<string> _categories = new();
    private readonly ObservableCollection<string> _scanFolders = new();
    private readonly List<CatFolderRow> _catFolders = new();
    private readonly List<CheckBox> _driveChecks = new();
    private readonly StackPanel _catFolderPanel = new();

    /// <summary>Raised when the user saves; carries the new settings.</summary>
    public event Action<AppSettings>? Saved;

    public SettingsWindow(AppSettings settings, IReadOnlyList<string> knownCategories,
        IReadOnlyList<string> driveRoots)
    {
        _incoming = settings;
        _knownCategories = knownCategories;
        _driveRoots = driveRoots;

        Title = "Settings"; Width = 720; Height = 760;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _apiKey.Text = settings.TmdbApiKey;
        _readToken.Text = settings.TmdbReadAccessToken;
        _startup.IsChecked = settings.StartWithWindows;
        _startInTray.IsChecked = settings.StartInTray;
        _startInTray.IsEnabled = settings.StartWithWindows;
        _startup.Checked += (_, _) => _startInTray.IsEnabled = true;
        _startup.Unchecked += (_, _) => _startInTray.IsEnabled = false;
        _watch.IsChecked = settings.WatchForNewFiles;
        _rememberFilters.IsChecked = settings.RememberFilters;
        _excludeSystem.IsChecked = settings.ExcludeSystemDirectories;
        foreach (var e in settings.IgnoredExtensions) _exts.Add(e);
        foreach (var f in settings.ExcludedFolders) _excluded.Add(new ExclRow { Model = f });
        foreach (var c in settings.CustomCategories) _categories.Add(c);
        foreach (var f in settings.AdditionalScanFolders) _scanFolders.Add(f);

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
        // Explicit Close rather than IsCancel: a non-modal window has no dialog result.
        var cancel = new Button { Content = "Close", Width = 84 };
        cancel.Click += (_, _) => Close();
        buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        var panel = new StackPanel();
        panel.Children.Add(Section("themoviedb.org"));
        panel.Children.Add(Labeled("API key (v3):", _apiKey));
        panel.Children.Add(Labeled("Read token (v4):", _readToken));
        panel.Children.Add(Hint("Get free credentials at themoviedb.org → account settings → API. Either the v4 Read Access Token or the v3 API Key works (the token is preferred). Queries are rate-limited to one every two seconds."));

        panel.Children.Add(Section("Startup & watching"));
        _startup.Margin = new Thickness(0, 2, 0, 2);
        _watch.Margin = new Thickness(0, 2, 0, 2);
        _rememberFilters.Margin = new Thickness(0, 2, 0, 2);
        panel.Children.Add(_startup);
        panel.Children.Add(_startInTray);
        panel.Children.Add(_watch);
        panel.Children.Add(DriveWatchEditor(settings));
        panel.Children.Add(_rememberFilters);

        panel.Children.Add(Section("Folders scanned in addition to whole drives"));
        panel.Children.Add(ScanFolderEditor());
        panel.Children.Add(Hint("A download folder, say. These are watched along with the drives, and can be " +
                                "rescanned on their own with \"Scan folder…\" without re-walking a whole drive."));

        panel.Children.Add(Section("Consolidation folders (one per category)"));
        panel.Children.Add(CategoryFolderEditor(settings));
        panel.Children.Add(Hint("A consolidation folder is the central location scattered files are moved into, e.g. all TV under T:\\TV\\. " +
                                "TV goes to <folder>\\<A-Z or #>\\<Show>\\Season NN\\ with episodes renamed \"01 - name.ext\"; films to <folder>\\<A-Z or #>\\<Title (Year)>\\. " +
                                "Specials and featurettes follow their show or film into an Extras subfolder."));

        panel.Children.Add(Section("Ignored file types (removed from results and skipped in future scans)"));
        panel.Children.Add(ListEditor(_exts, addPrompt: "Extension e.g. .nfo", onAdd: AddExtension));

        panel.Children.Add(Section("Excluded folders"));
        _excludeSystem.Margin = new Thickness(0, 2, 0, 6);
        panel.Children.Add(_excludeSystem);
        panel.Children.Add(ExcludedEditor());
        panel.Children.Add(Hint(@"Paths may be exact folders (D:\Downloads) or patterns: * matches any run of characters, ? matches one. " +
                                @"So *\Windows\* excludes every Windows folder on every drive, and ?:\$Recycle.Bin excludes the bin on all of them."));

        panel.Children.Add(Section("Custom categories"));
        panel.Children.Add(ListEditor(_categories, addPrompt: "New category name", onAdd: AddCategory));

        root.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = panel
        });
        Content = root;

        // Esc closes, as it would for a modal dialog.
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
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

    private FrameworkElement ListEditor(ObservableCollection<string> items, string addPrompt, Action<string> onAdd)
    {
        var dp = new DockPanel();
        var list = new ListBox { Height = 90, ItemsSource = items };

        var controls = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        var input = new TextBox { Width = 260, VerticalContentAlignment = VerticalAlignment.Center, ToolTip = addPrompt };
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
        return dp;
    }

    // --- Drives to watch --------------------------------------------------

    private FrameworkElement DriveWatchEditor(AppSettings settings)
    {
        var wrap = new StackPanel { Margin = new Thickness(20, 4, 0, 0) };
        wrap.Children.Add(new TextBlock
        {
            Text = "Drives to watch (none ticked = every drive that was scanned):",
            Margin = new Thickness(0, 0, 0, 2)
        });

        // Offer the drives currently attached, plus any saved one that is not present now
        // so an unplugged drive's setting is not silently lost.
        var roots = _driveRoots
            .Concat(settings.WatchedDrives)
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(r => r, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var panel = new WrapPanel();
        foreach (var root in roots)
        {
            var cb = new CheckBox
            {
                Content = root,
                Tag = root,
                Margin = new Thickness(0, 2, 14, 2),
                IsChecked = settings.WatchedDrives.Contains(root, StringComparer.OrdinalIgnoreCase)
            };
            _driveChecks.Add(cb);
            panel.Children.Add(cb);
        }
        if (roots.Count == 0)
            panel.Children.Add(new TextBlock { Text = "(no drives available)", Foreground = System.Windows.Media.Brushes.Gray });
        wrap.Children.Add(panel);
        return wrap;
    }

    private FrameworkElement ScanFolderEditor()
    {
        var wrap = new StackPanel();
        var list = new ListBox { Height = 70, ItemsSource = _scanFolders };
        wrap.Children.Add(list);

        var controls = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        var add = new Button { Content = "Add folder…", Width = 100 };
        add.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog { Title = "Choose a folder to scan" };
            if (dlg.ShowDialog(this) == true &&
                !_scanFolders.Contains(dlg.FolderName, StringComparer.OrdinalIgnoreCase))
                _scanFolders.Add(dlg.FolderName);
        };
        controls.Children.Add(add);

        var remove = new Button { Content = "Remove", Width = 80, Margin = new Thickness(6, 0, 0, 0) };
        remove.Click += (_, _) => { if (list.SelectedItem is string s) _scanFolders.Remove(s); };
        controls.Children.Add(remove);
        wrap.Children.Add(controls);
        return wrap;
    }

    // --- Excluded folders -------------------------------------------------

    private FrameworkElement ExcludedEditor()
    {
        var wrap = new StackPanel();
        var list = new ListBox { Height = 100, ItemsSource = _excluded, DisplayMemberPath = nameof(ExclRow.Display) };
        wrap.Children.Add(list);

        var controls = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        var subdirs = new CheckBox { Content = "incl. subfolders", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        controls.Children.Add(subdirs);

        var browse = new Button { Content = "Add folder…", Width = 96 };
        browse.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog { Title = "Choose folder to exclude" };
            if (dlg.ShowDialog(this) == true)
                AddExclusion(dlg.FolderName, subdirs.IsChecked == true);
        };
        controls.Children.Add(browse);

        // Typed rules are the only way to enter a pattern — a folder browser can't
        // produce "*\Windows\*", which matches many folders and exists as none.
        var addPath = new Button { Content = "Add path or pattern…", Width = 150, Margin = new Thickness(6, 0, 0, 0) };
        addPath.Click += (_, _) =>
        {
            var typed = PromptWindow.Ask(this, "Exclude path or pattern",
                @"Folder path, or a pattern (* = any characters, ? = one). Examples:" + "\n" +
                @"    D:\Downloads\Temp" + "\n" +
                @"    *\Windows\*" + "\n" +
                @"    ?:\$Recycle.Bin");
            if (!string.IsNullOrWhiteSpace(typed))
                AddExclusion(typed.Trim(), subdirs.IsChecked == true);
        };
        controls.Children.Add(addPath);

        var edit = new Button { Content = "Edit…", Width = 70, Margin = new Thickness(6, 0, 0, 0) };
        edit.Click += (_, _) =>
        {
            if (list.SelectedItem is not ExclRow r) return;
            var newPath = PromptWindow.Ask(this, "Edit excluded folder",
                "Folder path or pattern (wildcards allowed):", r.Model.Path);
            if (string.IsNullOrWhiteSpace(newPath)) return;
            var path = newPath.Trim();
            if (!ConfirmPath(path)) return;

            r.Model.Path = path;
            var idx = _excluded.IndexOf(r);
            _excluded.RemoveAt(idx);
            _excluded.Insert(idx, new ExclRow { Model = r.Model }); // refresh the display
        };
        controls.Children.Add(edit);

        var remove = new Button { Content = "Remove", Width = 80, Margin = new Thickness(6, 0, 0, 0) };
        remove.Click += (_, _) => { if (list.SelectedItem is ExclRow r) _excluded.Remove(r); };
        controls.Children.Add(remove);
        wrap.Children.Add(controls);
        return wrap;
    }

    /// <summary>
    /// A plain path that doesn't exist is usually a typo, so confirm it. Patterns are
    /// accepted as they are: they are meant to match folders rather than be one.
    /// </summary>
    private bool ConfirmPath(string path)
    {
        if (!AppSettings.IsQuestionablePath(path)) return true;
        return MessageBox.Show(this,
            $"This folder does not exist and contains no wildcard:\n\n{path}\n\n" +
            "Patterns like *\\Windows\\* match many folders; a plain path must exist to " +
            "match anything. Add it anyway?",
            "Folder not found", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }

    /// <summary>Add an exclusion, offering to prune any narrower rules it now supersedes.</summary>
    private void AddExclusion(string path, bool includeSubdirs)
    {
        if (!ConfirmPath(path)) return;

        var candidate = new ExcludedFolder { Path = path, IncludeSubdirectories = includeSubdirs };
        var probe = new AppSettings { ExcludedFolders = _excluded.Select(r => r.Model).ToList() };
        var superseded = probe.FindSupersededBy(candidate);
        if (superseded.Count > 0)
        {
            var ask = MessageBox.Show(this,
                $"This rule already covers {superseded.Count} more specific excluded folder(s):\n\n" +
                string.Join("\n", superseded.Select(s => s.Path)) +
                "\n\nRemove the redundant rules?",
                "Redundant rules", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (ask == MessageBoxResult.Yes)
                foreach (var s in superseded)
                {
                    var row = _excluded.FirstOrDefault(r => ReferenceEquals(r.Model, s));
                    if (row != null) _excluded.Remove(row);
                }
        }
        _excluded.Add(new ExclRow { Model = candidate });
    }

    // --- Consolidation folders per category -------------------------------

    private FrameworkElement CategoryFolderEditor(AppSettings settings)
    {
        var wrap = new StackPanel();
        wrap.Children.Add(_catFolderPanel);

        foreach (var cf in settings.CategoryFolders)
            AddCategoryFolderRow(cf.Category, cf.Folder);
        if (_catFolders.Count == 0)
        {
            // A fresh install starts with the two categories everyone consolidates.
            AddCategoryFolderRow("TvShow", settings.TvConsolidationDir);
            AddCategoryFolderRow("Movie", settings.FilmConsolidationDir);
        }

        var add = new Button
        {
            Content = "Add category folder", Width = 150,
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0)
        };
        add.Click += (_, _) => AddCategoryFolderRow("", "");
        wrap.Children.Add(add);
        return wrap;
    }

    private void AddCategoryFolderRow(string category, string folder)
    {
        var dp = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };

        var combo = new ComboBox
        {
            IsEditable = true, Width = 130, ItemsSource = CategoryChoices(),
            Text = category, VerticalContentAlignment = VerticalAlignment.Center
        };
        DockPanel.SetDock(combo, Dock.Left);
        dp.Children.Add(combo);

        var remove = new Button { Content = "✕", Width = 26, Margin = new Thickness(6, 0, 0, 0), ToolTip = "Remove this row" };
        DockPanel.SetDock(remove, Dock.Right);
        dp.Children.Add(remove);

        var browse = new Button { Content = "Browse…", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(6, 0, 0, 0) };
        DockPanel.SetDock(browse, Dock.Right);
        dp.Children.Add(browse);

        var box = new TextBox { Text = folder, Margin = new Thickness(6, 0, 0, 0), VerticalContentAlignment = VerticalAlignment.Center };
        dp.Children.Add(box);

        browse.Click += (_, _) =>
        {
            var name = string.IsNullOrWhiteSpace(combo.Text) ? "this category" : $"'{combo.Text.Trim()}'";
            var dlg = new OpenFolderDialog { Title = $"Consolidation folder for {name}" };
            if (dlg.ShowDialog(this) == true) box.Text = dlg.FolderName;
        };

        var row = new CatFolderRow { Category = combo, Folder = box, Container = dp };
        remove.Click += (_, _) =>
        {
            _catFolders.Remove(row);
            _catFolderPanel.Children.Remove(dp);
        };

        _catFolders.Add(row);
        _catFolderPanel.Children.Add(dp);
    }

    /// <summary>Categories offered in the row combos: the known ones plus any just added.</summary>
    private List<string> CategoryChoices() =>
        _knownCategories.Concat(_categories)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

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
        var folders = new List<CategoryConsolidation>();
        foreach (var row in _catFolders)
        {
            var category = row.Category.Text.Trim();
            var folder = row.Folder.Text.Trim();
            if (category.Length == 0 || folder.Length == 0) continue;
            // Last row wins if a category is listed twice.
            folders.RemoveAll(f => string.Equals(f.Category, category, StringComparison.OrdinalIgnoreCase));
            folders.Add(new CategoryConsolidation { Category = category, Folder = folder });
        }

        var result = new AppSettings
        {
            TmdbApiKey = _apiKey.Text.Trim(),
            TmdbReadAccessToken = _readToken.Text.Trim(),
            StartWithWindows = _startup.IsChecked == true,
            StartInTray = _startInTray.IsChecked == true,
            WatchForNewFiles = _watch.IsChecked == true,
            WatchedDrives = _driveChecks.Where(c => c.IsChecked == true)
                .Select(c => (string)c.Tag).ToList(),
            RememberFilters = _rememberFilters.IsChecked == true,
            ExcludeSystemDirectories = _excludeSystem.IsChecked == true,
            IgnoredExtensions = _exts.ToList(),
            ExcludedFolders = _excluded.Select(r => r.Model).ToList(),
            CustomCategories = _categories.ToList(),
            CategoryFolders = folders,
            AdditionalScanFolders = _scanFolders.ToList(),
            ColumnLayouts = _incoming.ColumnLayouts,            // owned by the grid
            LastFilterMode = _incoming.LastFilterMode,          // owned by the filter bar
            LastFilterColumn = _incoming.LastFilterColumn,
            LastFilterPattern = _incoming.LastFilterPattern,
            LastFilterNegate = _incoming.LastFilterNegate,
            SavedFilters = _incoming.SavedFilters,
            FolderCategoryRules = _incoming.FolderCategoryRules, // preserved
            FolderTitleRules = _incoming.FolderTitleRules
        };
        result.SyncLegacyFolders();

        Saved?.Invoke(result);
        Close();
    }
}
