using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using MediaCatalog.App.Infrastructure;
using MediaCatalog.App.ViewModels;
using MediaCatalog.Core.Models;
using MediaCatalog.Core.Scanning;
using MediaCatalog.Core.Storage;
using Microsoft.Win32;

namespace MediaCatalog.App;

/// <summary>
/// Walks the user through setting a scan up: what to do with what is already catalogued,
/// where to look, what to pick up, and what to do about a drive that is not plugged in.
///
/// This is where choosing drives lives now. It was a panel taking up a third of the main
/// window at all times, for a decision made once in a while — and with nowhere to explain
/// the options that go with it.
/// </summary>
public class ScanWizardWindow : Window
{
    /// <summary>A folder offered alongside the drives, remembered between runs.</summary>
    private sealed class FolderChoice : ObservableObject
    {
        private bool _selected = true;
        public required string Path { get; init; }
        public bool IsSelected { get => _selected; set => SetProperty(ref _selected, value); }
        public string Display => Directory.Exists(Path) ? Path : Path + "   (not found)";
    }

    private readonly AppSettings _settings;
    private readonly int _catalogued;

    private readonly List<DriveItem> _drives = new();
    private readonly ObservableCollection<FolderChoice> _folders = new();

    private readonly RadioButton _addToExisting;
    private readonly RadioButton _startFresh;
    private readonly ComboBox _mediaFilter = new() { Width = 160 };
    private readonly TextBox _minSize = new() { Width = 100 };
    private readonly TextBox _maxSize = new() { Width = 100 };
    private readonly RadioButton _waitForDrives;
    private readonly RadioButton _skipMissingDrives;
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _missingPanel = new();

    private readonly ContentControl _page = new();
    private readonly TextBlock _heading = new()
    {
        FontSize = 16, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 2)
    };
    private readonly TextBlock _subheading = new()
    {
        Foreground = System.Windows.Media.Brushes.Gray, TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 0, 0, 10)
    };
    private readonly Button _back = new() { Content = "Back", Width = 90, Margin = new Thickness(0, 0, 6, 0) };
    private readonly Button _next = new() { Content = "Next", Width = 90, Margin = new Thickness(0, 0, 6, 0), IsDefault = true };
    private readonly Button _start = new()
    {
        Content = "Start scan", Width = 110, FontWeight = FontWeights.Bold,
        Margin = new Thickness(0, 0, 6, 0), Visibility = Visibility.Collapsed
    };

    private readonly List<(string Heading, string Sub, Func<FrameworkElement> Build)> _pages = new();

    /// <summary>
    /// Each page, built the first time it is reached and kept. The pages share controls —
    /// the same combo box is the answer whichever way you walked to it — and a control can
    /// only have one parent, so rebuilding a page on the way back would tear the previous
    /// one apart.
    /// </summary>
    private readonly Dictionary<int, FrameworkElement> _built = new();
    private int _index;

    /// <summary>What the user settled on, or null if they backed out.</summary>
    public ScanPlan? Plan { get; private set; }

    public ScanWizardWindow(AppSettings settings, int cataloguedFiles, IReadOnlyList<string> lastDrives)
    {
        _settings = settings;
        _catalogued = cataloguedFiles;

        Title = "Scan"; Width = 700; Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _addToExisting = new RadioButton
        {
            Content = $"Add to the existing catalogue ({cataloguedFiles:N0} file(s))",
            IsChecked = cataloguedFiles > 0,
            IsEnabled = cataloguedFiles > 0,
            Margin = new Thickness(0, 6, 0, 2)
        };
        _startFresh = new RadioButton
        {
            Content = "Start again from nothing — throw the current catalogue away",
            IsChecked = cataloguedFiles == 0,
            Margin = new Thickness(0, 8, 0, 2)
        };

        _waitForDrives = new RadioButton
        {
            Content = "Wait for it, and scan it as soon as it is connected",
            Margin = new Thickness(0, 6, 0, 2)
        };
        _skipMissingDrives = new RadioButton
        {
            Content = "Scan the rest now and leave that drive alone",
            IsChecked = true,
            Margin = new Thickness(0, 2, 0, 2)
        };

        BuildDriveList(lastDrives);
        foreach (var folder in settings.AdditionalScanFolders)
            _folders.Add(new FolderChoice { Path = folder });

        _mediaFilter.ItemsSource = Enum.GetValues(typeof(ScanMediaFilter));
        _mediaFilter.SelectedItem = settings.ScanMediaFilter;
        _minSize.Text = FormatSize(settings.MinFileSizeBytes);
        _maxSize.Text = FormatSize(settings.MaxFileSizeBytes);

        _pages.Add(("What should this scan do?",
            "A scan can add to what is already known, or start over. Starting over is the " +
            "right choice after changing the size limits or the media filter, since the old " +
            "catalogue was built under the old rules.",
            StartModePage));
        _pages.Add(("Where should it look?",
            "Tick the drives to walk. Whole drives take a while the first time and are quick " +
            "afterwards — only new and changed files are hashed again.",
            LocationsPage));
        _pages.Add(("What should it pick up?",
            "Narrowing a scan makes it faster. Nothing it was not looking for is ever removed " +
            "from the catalogue, so an audio pass and a video pass build one library between them.",
            ScopePage));
        _pages.Add(("Ready to scan",
            "One last look before it starts. A scan can be paused at any point and resumed later " +
            "without losing the hashing it has already done.",
            SummaryPage));

        Content = BuildShell();
        ShowPage(0);
    }

    // --- Shell ------------------------------------------------------------

    private FrameworkElement BuildShell()
    {
        var dock = new DockPanel { Margin = new Thickness(16) };

        var header = new StackPanel();
        header.Children.Add(_heading);
        header.Children.Add(_subheading);
        DockPanel.SetDock(header, Dock.Top);
        dock.Children.Add(header);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        _back.Click += (_, _) => ShowPage(_index - 1);
        _next.Click += (_, _) => ShowPage(_index + 1);
        _start.Click += (_, _) => Finish();
        buttons.Children.Add(_back);
        buttons.Children.Add(_next);
        buttons.Children.Add(_start);
        buttons.Children.Add(new Button { Content = "Cancel", Width = 90, IsCancel = true });
        DockPanel.SetDock(buttons, Dock.Bottom);
        dock.Children.Add(buttons);

        dock.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _page
        });
        return dock;
    }

    private void ShowPage(int index)
    {
        _index = Math.Clamp(index, 0, _pages.Count - 1);
        var (heading, sub, build) = _pages[_index];
        _heading.Text = heading;
        _subheading.Text = sub;

        if (!_built.TryGetValue(_index, out var content))
            _built[_index] = content = build();
        _page.Content = content;

        var last = _index == _pages.Count - 1;
        // The summary describes choices made on the pages behind it, so it is brought up
        // to date every time it is reached rather than only when it was first built.
        if (last) RefreshSummary();
        _back.IsEnabled = _index > 0;
        _next.Visibility = last ? Visibility.Collapsed : Visibility.Visible;
        _start.Visibility = last ? Visibility.Visible : Visibility.Collapsed;
        _next.IsDefault = !last;
        _start.IsDefault = last;
    }

    // --- Pages ------------------------------------------------------------

    private FrameworkElement StartModePage()
    {
        var panel = new StackPanel();
        panel.Children.Add(_addToExisting);
        panel.Children.Add(Hint("Files already catalogued keep their titles, categories, hashes and " +
                                "fingerprints. Only what has appeared or changed since is read again.", 20));
        panel.Children.Add(_startFresh);
        panel.Children.Add(Hint("Everything currently catalogued is discarded — titles you have typed, " +
                                "categories you have set, fingerprints that took hours. Files on disk are " +
                                "not touched. Undo history is cleared.", 20));

        if (_catalogued == 0)
            panel.Children.Add(Hint("\nNothing is catalogued yet, so there is nothing to add to — this " +
                                    "first scan builds the catalogue."));
        return panel;
    }

    private FrameworkElement LocationsPage()
    {
        var panel = new StackPanel();

        panel.Children.Add(new TextBlock { Text = "Drives", FontWeight = FontWeights.Bold });
        var driveList = new ListBox
        {
            Height = 190,
            ItemsSource = _drives,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        driveList.ItemTemplate = CheckTemplate(nameof(DriveItem.IsSelected), nameof(DriveItem.Display));
        panel.Children.Add(driveList);

        var driveButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        driveButtons.Children.Add(Btn("All", () => SetAllDrives(true)));
        driveButtons.Children.Add(Btn("None", () => SetAllDrives(false)));
        panel.Children.Add(driveButtons);

        panel.Children.Add(new TextBlock
        {
            Text = "Folders", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 14, 0, 0)
        });
        panel.Children.Add(Hint("Scanned as well as the drives — a downloads folder, say. A folder that " +
                                "already sits on a ticked drive is covered by it and skipped."));
        var folderList = new ListBox
        {
            Height = 110,
            ItemsSource = _folders,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        folderList.ItemTemplate = CheckTemplate(nameof(FolderChoice.IsSelected), nameof(FolderChoice.Display));
        panel.Children.Add(folderList);

        var folderButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        folderButtons.Children.Add(Btn("Add folder…", () =>
        {
            var dlg = new OpenFolderDialog { Title = "Choose a folder to scan" };
            if (dlg.ShowDialog(this) == true &&
                _folders.All(f => !string.Equals(f.Path, dlg.FolderName, StringComparison.OrdinalIgnoreCase)))
                _folders.Add(new FolderChoice { Path = dlg.FolderName });
        }, 110));
        folderButtons.Children.Add(Btn("Remove", () =>
        {
            if (folderList.SelectedItem is FolderChoice f) _folders.Remove(f);
        }));
        panel.Children.Add(folderButtons);
        return panel;
    }

    private FrameworkElement ScopePage()
    {
        var panel = new StackPanel();

        panel.Children.Add(Labeled("Scan for:", _mediaFilter));
        panel.Children.Add(Hint("All picks up audio and video alike. VideoOnly and AudioOnly leave the " +
                                "other kind entirely alone — neither catalogued nor removed."));

        panel.Children.Add(new TextBlock
        {
            Text = "Size limits", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 14, 0, 4)
        });
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock
        {
            Text = "at least", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0)
        });
        row.Children.Add(_minSize);
        row.Children.Add(new TextBlock
        {
            Text = "at most", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 6, 0)
        });
        row.Children.Add(_maxSize);
        panel.Children.Add(row);
        panel.Children.Add(Hint("Write a plain number of bytes or a size like 50MB, 1.5 GB, 700 KB. Leave " +
                                "a box empty for no limit. A minimum of a few megabytes is the usual way " +
                                "to keep thumbnails and sound effects out of a film library."));

        panel.Children.Add(Hint("\nExcluded folders and ignored file types apply to every scan and are set " +
                                "on the Exclusions tab in Settings."));
        return panel;
    }

    private FrameworkElement SummaryPage()
    {
        var panel = new StackPanel();
        panel.Children.Add(_summary);
        panel.Children.Add(_missingPanel);
        RefreshSummary();
        return panel;
    }

    /// <summary>
    /// Restate the plan and re-check which chosen drives are actually attached — both
    /// depend on pages the user may have gone back and changed.
    /// </summary>
    private void RefreshSummary()
    {
        _summary.Text = DescribePlan();

        _missingPanel.Children.Clear();
        var missing = SelectedDrives().Where(d => !ScanEngine.IsRootAvailable(d)).ToList();
        if (missing.Count == 0) return;

        _missingPanel.Children.Add(new TextBlock
        {
            Text = $"⚠ Not connected: {string.Join(", ", missing)}",
            FontWeight = FontWeights.Bold, Margin = new Thickness(0, 14, 0, 2),
            Foreground = System.Windows.Media.Brushes.DarkOrange, TextWrapping = TextWrapping.Wrap
        });
        _missingPanel.Children.Add(Hint("Nothing already catalogued on that drive is removed either " +
                                        "way — a drive that is not plugged in has not become empty."));
        _missingPanel.Children.Add(_waitForDrives);
        _missingPanel.Children.Add(_skipMissingDrives);
    }

    private string DescribePlan()
    {
        var drives = SelectedDrives();
        var folders = SelectedFolders();
        var lines = new List<string>
        {
            _startFresh.IsChecked == true
                ? "• Building a new catalogue from nothing — the current one is discarded."
                : $"• Adding to the existing catalogue of {_catalogued:N0} file(s).",
            drives.Count > 0
                ? "• Drives: " + string.Join(", ", drives)
                : "• Drives: none chosen",
            folders.Count > 0 ? "• Folders: " + string.Join(", ", folders) : "• Folders: none",
            $"• Looking for: {_mediaFilter.SelectedItem}",
        };

        var min = ParseSize(_minSize.Text);
        var max = ParseSize(_maxSize.Text);
        var limits = (min is > 0 ? $"at least {FormatSize(min.Value)}" : "no minimum") + ", " +
                     (max is > 0 ? $"at most {FormatSize(max.Value)}" : "no maximum");
        lines.Add("• Size: " + limits);

        return string.Join("\n", lines);
    }

    // --- Result -----------------------------------------------------------

    private void Finish()
    {
        var drives = SelectedDrives();
        var folders = SelectedFolders();
        if (drives.Count == 0 && folders.Count == 0)
        {
            MessageBox.Show(this, "Tick at least one drive or folder to scan.",
                "Scan", MessageBoxButton.OK, MessageBoxImage.Information);
            ShowPage(1);
            return;
        }

        var min = ParseSize(_minSize.Text);
        var max = ParseSize(_maxSize.Text);
        if (min == null || max == null)
        {
            MessageBox.Show(this,
                "A size limit could not be read. Write a plain number of bytes, or a size like " +
                "50MB, 1.5 GB or 700 KB. Leave the box empty for no limit.",
                "Scan", MessageBoxButton.OK, MessageBoxImage.Warning);
            ShowPage(2);
            return;
        }
        if (min > 0 && max > 0 && min > max)
        {
            MessageBox.Show(this,
                "The smallest size is larger than the largest, so no file could ever match.",
                "Scan", MessageBoxButton.OK, MessageBoxImage.Warning);
            ShowPage(2);
            return;
        }

        if (_startFresh.IsChecked == true && _catalogued > 0)
        {
            var confirm = MessageBox.Show(this,
                $"Discard the catalogue of {_catalogued:N0} file(s) and start again?\n\n" +
                "Titles you have typed, categories you have set and fingerprints already " +
                "computed all go with it. Files on disk are not touched.",
                "Start again", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.OK) return;
        }

        Plan = new ScanPlan(
            drives,
            folders,
            (ScanMediaFilter)(_mediaFilter.SelectedItem ?? ScanMediaFilter.All),
            _startFresh.IsChecked == true ? ScanStartMode.StartFresh : ScanStartMode.AddToExisting,
            _waitForDrives.IsChecked == true)
        {
            MinSizeBytes = min.Value,
            MaxSizeBytes = max.Value
        };
        DialogResult = true;
    }

    // --- Bits and pieces --------------------------------------------------

    private void BuildDriveList(IReadOnlyList<string> lastDrives)
    {
        var chosen = lastDrives.ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var root in DriveScanner.GetAvailableDrives())
            _drives.Add(new DriveItem(root) { IsSelected = chosen.Contains(root.Path) });

        // Drives chosen last time that are not attached now stay on the list rather than
        // vanishing: an external drive is still part of the library while it is unplugged.
        foreach (var path in lastDrives.Where(p =>
                     _drives.All(d => !string.Equals(d.Path, p, StringComparison.OrdinalIgnoreCase))))
            _drives.Add(new DriveItem(new ScanRoot(path, "not connected", 0, 0)) { IsSelected = true });
    }

    private void SetAllDrives(bool selected)
    {
        foreach (var d in _drives) d.IsSelected = selected;
    }

    private List<string> SelectedDrives() =>
        _drives.Where(d => d.IsSelected).Select(d => d.Path).ToList();

    private List<string> SelectedFolders() =>
        _folders.Where(f => f.IsSelected).Select(f => f.Path).ToList();

    private static DataTemplate CheckTemplate(string checkedPath, string textPath)
    {
        var check = new FrameworkElementFactory(typeof(CheckBox));
        check.SetBinding(ToggleButtonIsCheckedProperty,
            new System.Windows.Data.Binding(checkedPath)
            {
                Mode = System.Windows.Data.BindingMode.TwoWay,
                UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged
            });
        check.SetBinding(ContentControl.ContentProperty, new System.Windows.Data.Binding(textPath));
        check.SetValue(MarginProperty, new Thickness(2));
        return new DataTemplate { VisualTree = check };
    }

    private static readonly DependencyProperty ToggleButtonIsCheckedProperty =
        System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty;

    private static Button Btn(string text, Action onClick, double width = 70)
    {
        var b = new Button { Content = text, Width = width, Margin = new Thickness(0, 0, 6, 0) };
        b.Click += (_, _) => onClick();
        return b;
    }

    private static TextBlock Hint(string text, double indent = 0) => new()
    {
        Text = text, Foreground = System.Windows.Media.Brushes.Gray,
        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(indent, 2, 0, 6)
    };

    private static FrameworkElement Labeled(string label, FrameworkElement control)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock
        {
            Text = label, Width = 90, VerticalAlignment = VerticalAlignment.Center
        });
        row.Children.Add(control);
        return row;
    }

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0) return string.Empty;
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        var unit = 0;
        double value = bytes;
        while (value >= 1024 && unit < units.Length - 1 && value % 1024 == 0)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.##} {units[unit]}";
    }

    private static long? ParseSize(string text) => SettingsWindow.ParseSize(text);
}
