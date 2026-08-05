using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MediaCatalog.Core.Classification;
using MediaCatalog.Core.Consolidation;
using MediaCatalog.Core.Models;
using MediaCatalog.Core.Storage;
using MediaCatalog.Core.Tools;
using Microsoft.Win32;

namespace MediaCatalog.App;

/// <summary>The tabs, in the order they are shown.</summary>
public enum SettingsTab
{
    General = 0,
    Scanning,
    Library,
    Exclusions,
    Categories,
    ExternalTools,
    DataSources
}

/// <summary>
/// Edits <see cref="AppSettings"/> and the external-tool paths in one place.
///
/// The tabs run from what changes often to what changes once: which folders to watch and
/// how the app starts are revisited regularly, while an API key is typed in on the day it
/// is obtained and rarely looked at again. Everything else falls between the two.
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
        public required TextBox NameTemplate { get; init; }
        public required FrameworkElement Container { get; init; }
    }

    private readonly AppSettings _incoming;
    private readonly ToolSettings _incomingTools;
    private readonly IReadOnlyList<string> _knownCategories;
    private readonly IReadOnlyList<string> _driveRoots;
    private readonly Func<Task<string>>? _downloadImdb;
    private readonly Func<Task<string>>? _downloadImdbEpisodes;

    private readonly TabControl _tabs = new();

    // --- themoviedb.org / IMDb ---
    private readonly TextBox _apiKey = new();
    private readonly TextBox _readToken = new();
    private readonly TextBox _imdbUrl = new();
    private readonly TextBox _imdbEpisodeUrl = new();
    private readonly TextBlock _imdbStatus;

    // The window of release years worth extracting. Very few people collect media from
    // every year there has been one, and every row left out is a row nothing has to read.
    private readonly TextBox _extractFrom = new() { Width = 70 };
    private readonly TextBox _extractTo = new() { Width = 70 };

    // --- Startup / window / watching ---
    private readonly CheckBox _startup = new() { Content = "Start Media Catalog when Windows starts" };
    private readonly CheckBox _startInTray = new()
    {
        Content = "…and start hidden in the notification area, without opening the window",
        Margin = new Thickness(20, 2, 0, 2)
    };
    private readonly CheckBox _alwaysMinimised = new()
    {
        Content = "Always start minimised to the notification area, however it was launched"
    };
    private readonly CheckBox _minimiseToTray = new()
    {
        Content = "Minimising sends the window to the notification area instead of the taskbar"
    };
    private readonly CheckBox _watch = new() { Content = "Watch for new files and add them (with a taskbar notification)" };

    // --- Scan limits ---
    private readonly TextBox _minSize = new() { Width = 90 };
    private readonly TextBox _maxSize = new() { Width = 90 };
    private readonly ComboBox _scanFilter = new() { Width = 150 };
    private readonly ComboBox _progressName = new() { Width = 190 };

    // --- IMDb ---
    private readonly CheckBox _useImdbFirst = new()
    {
        Content = "Check the local IMDb data before TMDb (TMDb is only asked what IMDb cannot answer)"
    };
    private readonly CheckBox _imdbInMemory = new()
    {
        Content = "Keep the IMDb data in memory (faster lookups; roughly 200–400 MB)"
    };
    private readonly CheckBox _rememberFilters = new()
    {
        Content = "Remember the view and filters between sessions"
    };
    private readonly CheckBox _renameOnTitle = new()
    {
        Content = "Rename files on disk when their title changes, to match the naming scheme"
    };
    private readonly CheckBox _excludeSystem = new()
    {
        Content = "Automatically exclude system directories (Windows, Program Files, $Recycle.Bin, …)"
    };
    private readonly ComboBox _redundantRules = new() { Width = 260 };

    // --- Deleting ---
    private readonly CheckBox _skipRecycleBin = new()
    {
        Content = "Open the Delete files dialog with \"Skip the Recycle Bin\" already ticked"
    };
    private readonly CheckBox _offerEmptyFolders = new()
    {
        Content = "After deleting the last file in a folder, offer to remove the folder too"
    };
    private readonly CheckBox _deleteFoldersPermanently = new()
    {
        Content = "Delete those folders outright rather than sending them to the Recycle Bin"
    };
    private readonly CheckBox _autoRemoveFolders = new()
    {
        Content = "Take away the folders a consolidation empties without asking"
    };

    // --- Subtitles ---
    private readonly CheckBox _consolidateSubtitles = new()
    {
        Content = "Bring subtitles along when their video is consolidated or moved"
    };

    // --- Watching ---
    private readonly TextBox _notifyDelay = new() { Width = 60 };

    // --- Ignored file types ---
    // One box rather than a list: twenty extensions down the side of the dialog is a column
    // of scrolling for something that fits on two lines written across.
    private readonly TextBox _extsBox = new()
    {
        AcceptsTab = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
        Height = 90, VerticalScrollBarVisibility = ScrollBarVisibility.Auto
    };

    // --- Titles and sorting ---
    private readonly CheckBox _capitaliseTitles = new()
    {
        Content = "Capitalise the first letter of every word in a title"
    };
    private readonly CheckBox _articleLast = new()
    {
        Content = "File \"The Simpsons\" under S as \"Simpsons (The)\""
    };
    private readonly ComboBox _doubleClick = new() { Width = 190 };
    private readonly CheckBox _probeDuringScan = new()
    {
        Content = "Read each file's length and quality during a scan (needs ffprobe)"
    };

    // --- External tools ---
    private readonly TextBox _ffmpeg = new() { VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBox _ffprobe = new() { VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBox _fpcalc = new() { VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBlock _toolStatus = new() { FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap };

    private readonly ObservableCollection<ExclRow> _excluded = new();
    private readonly ObservableCollection<string> _categories = new();
    private readonly ObservableCollection<string> _scanFolders = new();
    private readonly ObservableCollection<string> _watchFolders = new();
    private readonly ObservableCollection<string> _categoryOrder = new();
    private readonly List<CatFolderRow> _catFolders = new();
    private readonly List<CheckBox> _driveChecks = new();
    private readonly StackPanel _catFolderPanel = new();

    /// <summary>The per-category "what may be left behind" boxes, in the order they are shown.</summary>
    private readonly List<(string Category, TextBox Bytes)> _leftoverRows = new();

    /// <summary>The per-category "how far the lengths may disagree" boxes.</summary>
    private readonly List<(string Category, TextBox Seconds)> _toleranceRows = new();

    /// <summary>Raised when the user saves; carries the new settings.</summary>
    public event Action<AppSettings>? Saved;

    /// <summary>Raised alongside <see cref="Saved"/> with the external-tool paths.</summary>
    public event Action<ToolSettings>? ToolsSaved;

    /// <param name="downloadImdb">
    /// Fetches the IMDb dataset, for the button on the data tab. Null when the caller has
    /// no way to do that, in which case the button is not offered.
    /// </param>
    public SettingsWindow(
        AppSettings settings,
        IReadOnlyList<string> knownCategories,
        IReadOnlyList<string> driveRoots,
        ToolSettings tools,
        Func<Task<string>>? downloadImdb = null,
        Func<Task<string>>? downloadImdbEpisodes = null)
    {
        _incoming = settings;
        _incomingTools = tools;
        _knownCategories = knownCategories;
        _driveRoots = driveRoots;
        _downloadImdb = downloadImdb;
        _downloadImdbEpisodes = downloadImdbEpisodes;
        _imdbStatus = Hint(ImdbStatusText());

        Title = "Settings"; Width = 760; Height = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        LoadValues(settings, tools);

        var root = new DockPanel { Margin = new Thickness(14) };

        // Buttons pinned to the bottom, below the tabs, so Save always means "save it all".
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0)
        };
        var save = new Button
        {
            Content = "Save", Width = 84, IsDefault = true,
            FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 6, 0)
        };
        save.Click += OnSave;
        buttons.Children.Add(save);
        // Explicit Close rather than IsCancel: a non-modal window has no dialog result.
        var cancel = new Button { Content = "Close", Width = 84 };
        cancel.Click += (_, _) => Close();
        buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        _tabs.Items.Add(Tab("General", GeneralTab()));
        _tabs.Items.Add(Tab("Scanning", ScanningTab()));
        _tabs.Items.Add(Tab("Library", LibraryTab()));
        _tabs.Items.Add(Tab("Exclusions", ExclusionsTab()));
        _tabs.Items.Add(Tab("Categories", CategoriesTab()));
        _tabs.Items.Add(Tab("External tools", ToolsTab()));
        _tabs.Items.Add(Tab("Data sources", DataTab()));
        root.Children.Add(_tabs);

        Content = root;

        // Esc closes, as it would for a modal dialog.
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    /// <summary>Open on a particular tab — used when something sent the user here.</summary>
    public void Show(SettingsTab tab)
    {
        _tabs.SelectedIndex = (int)tab;
        Show();
    }

    /// <summary>Bring an already-open window forward, on the tab that matters now.</summary>
    public void FocusTab(SettingsTab tab)
    {
        _tabs.SelectedIndex = (int)tab;
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void LoadValues(AppSettings settings, ToolSettings tools)
    {
        _apiKey.Text = settings.TmdbApiKey;
        _readToken.Text = settings.TmdbReadAccessToken;
        _imdbUrl.Text = settings.EffectiveImdbDownloadUrl;
        _imdbEpisodeUrl.Text = settings.EffectiveImdbEpisodeDownloadUrl;
        _extractFrom.Text = settings.ExtractStartYear?.ToString() ?? "";
        _extractTo.Text = settings.ExtractEndYear?.ToString() ?? "";
        _startup.IsChecked = settings.StartWithWindows;
        _startInTray.IsChecked = settings.StartInTray;
        _startInTray.IsEnabled = settings.StartWithWindows;
        _startup.Checked += (_, _) => _startInTray.IsEnabled = true;
        _startup.Unchecked += (_, _) => _startInTray.IsEnabled = false;
        _alwaysMinimised.IsChecked = settings.AlwaysStartMinimised;
        _minimiseToTray.IsChecked = settings.MinimiseToTray;
        _watch.IsChecked = settings.WatchForNewFiles;
        _rememberFilters.IsChecked = settings.RememberFilters;
        _renameOnTitle.IsChecked = settings.RenameOnTitleChange;
        _excludeSystem.IsChecked = settings.ExcludeSystemDirectories;
        _minSize.Text = FormatSize(settings.MinFileSizeBytes);
        _maxSize.Text = FormatSize(settings.MaxFileSizeBytes);
        _useImdbFirst.IsChecked = settings.UseImdbFirst;
        _imdbInMemory.IsChecked = settings.ImdbInMemory;
        _skipRecycleBin.IsChecked = settings.SkipRecycleBinByDefault;
        _offerEmptyFolders.IsChecked = settings.OfferRemoveEmptyFolders;
        _deleteFoldersPermanently.IsChecked = settings.DeleteEmptyFoldersPermanently;
        _autoRemoveFolders.IsChecked = settings.RemoveEmptyFoldersAutomatically;
        _consolidateSubtitles.IsChecked = settings.ConsolidateSubtitles;
        _notifyDelay.Text = Math.Clamp(settings.NewFileNotifyDelaySeconds, 1, 600).ToString();
        _capitaliseTitles.IsChecked = settings.CapitaliseTitles;
        _articleLast.IsChecked = settings.SortLeadingArticleLast;
        _probeDuringScan.IsChecked = settings.ProbeDuringScan;

        _scanFilter.ItemsSource = Enum.GetValues(typeof(ScanMediaFilter));
        _scanFilter.SelectedItem = settings.ScanMediaFilter;
        _progressName.ItemsSource = Enum.GetValues(typeof(ProgressNamePosition));
        _progressName.SelectedItem = settings.ProgressNamePosition;
        _doubleClick.ItemsSource = new[] { "Play the file", "Open Edit details" };
        _doubleClick.SelectedIndex = (int)settings.DoubleClickAction;

        _redundantRules.ItemsSource = new[]
        {
            "Ask which redundant rules to remove",
            "Remove redundant rules automatically",
            "Leave redundant rules alone"
        };
        _redundantRules.SelectedIndex = (int)settings.RedundantExclusions;

        _ffmpeg.Text = tools.FfmpegPath;
        _ffprobe.Text = tools.FfprobePath;
        _fpcalc.Text = tools.FpcalcPath;

        // Tab-separated: they fit across the box rather than down a scrolling column.
        _extsBox.Text = string.Join("\t", settings.IgnoredExtensions);

        foreach (var f in settings.ExcludedFolders) _excluded.Add(new ExclRow { Model = f });
        foreach (var c in settings.CustomCategories) _categories.Add(c);
        foreach (var f in settings.AdditionalScanFolders) _scanFolders.Add(f);
        foreach (var f in settings.WatchedFolders) _watchFolders.Add(f);

        // The menu order, seeded from the categories that exist now so nothing is missing
        // from the list even if the saved order predates them.
        foreach (var c in CategoryResolver.Ordered(
                     _knownCategories.Concat(settings.CustomCategories)
                         .Where(c => !string.IsNullOrWhiteSpace(c))
                         .Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                     settings.CategoryOrder))
            _categoryOrder.Add(c);
    }

    // --- Tabs -------------------------------------------------------------

    private static TabItem Tab(string header, StackPanel content) => new()
    {
        Header = header,
        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(10, 4, 10, 10),
            Content = content
        }
    };

    private StackPanel GeneralTab()
    {
        var panel = new StackPanel();

        panel.Children.Add(Group("Startup and window",
            _startup, _startInTray, _alwaysMinimised, _minimiseToTray));

        panel.Children.Add(Group("Watching for new files",
            _watch,
            Labeled("Wait before saying:", _notifyDelay, 150),
            Hint("Seconds. Files arrive in handfuls — a folder copied in writes forty of them " +
                 "within a second of each other, and forty notifications about that is thirty-nine " +
                 "too many. The first arrival starts the wait and everything landing during it " +
                 "joins the same message. Later arrivals do not push the wait back, so news of a " +
                 "long copy is not held until it finishes."),
            DriveWatchEditor(_incoming)));

        panel.Children.Add(Group("Titles and names",
            _capitaliseTitles,
            Hint("Applies to titles worked out from a file name: \"the.matrix.1999.mkv\" reads " +
                 "*The Matrix*. A title confirmed against IMDb, or typed by you, is left exactly " +
                 "as it was spelled either way."),
            _renameOnTitle,
            Hint("A corrected title changes the name the file should have. With this on, the file " +
                 "on disk is renamed to match — \"Show - S01E02.mkv\" for an episode, " +
                 "\"Title (Year).mkv\" for a film — so the next scan reads the corrected name back " +
                 "rather than the old one.")));

        // Deliberately mild. Deleting a file for good already takes three deliberate acts —
        // choosing a delete, ticking the confirmation in the dialog, and pressing the
        // button — and this option is off unless somebody turns it on. A paragraph of
        // alarm on top of all that is shouting at the wrong moment.
        panel.Children.Add(Group("Deleting files",
            _skipRecycleBin,
            Warning("We don't recommend this."),
            _offerEmptyFolders,
            _autoRemoveFolders,
            Hint("With this on, the folders a consolidation empties simply go, and the run says how " +
                 "many did. There is nothing to decide: what goes has already been judged to be " +
                 "nothing, and a folder holding a catalogued file you have not filed yet, or a " +
                 "folder you have named anywhere in these settings, is never one of them. Turn it " +
                 "off to be shown the list and asked first."),
            _deleteFoldersPermanently,
            Hint("Safe in a way that deleting a file permanently is not: what goes has already " +
                 "been judged to be nothing — an empty folder, or one holding less than the size " +
                 "limit set for its category on the Library tab, with no catalogued file waiting " +
                 "to be filed anywhere in it. There is nothing in the Recycle Bin's job " +
                 "description for that.")));

        panel.Children.Add(Group("Behaviour",
            _rememberFilters,
            Labeled("Double-click:", _doubleClick, 120),
            Hint("What double-clicking a row in the results does: hand the file to whatever " +
                 "application Windows associates with it, or open Edit details to correct what " +
                 "the catalogue says about it."),
            Labeled("Progress name:", _progressName, 120),
            Hint("Where the current file name goes in the progress message — in every operation " +
                 "that works through files one at a time, not only a scan: verifying length and " +
                 "quality, re-hashing, moving, consolidating, analysing. Thousands of small files " +
                 "a second make a name on the right flicker the whole line; Left keeps the counter " +
                 "still, and Hidden leaves the name out.")));

        return panel;
    }

    private StackPanel ScanningTab()
    {
        var panel = new StackPanel();

        panel.Children.Add(Group("What a scan picks up",
            Labeled("Scan for:", _scanFilter, 110),
            Hint("VideoOnly and AudioOnly build one combined catalogue between them: a filtered " +
                 "scan never removes the kind it wasn't looking for."),
            SizeLimitEditor()));

        panel.Children.Add(Group("What a scan reads",
            _probeDuringScan,
            Hint("Fills in the Length and Quality columns as files are catalogued. It reads the " +
                 "container header rather than the file, so it costs a fraction of what hashing " +
                 "the same file costs, and entries that already know are skipped. Without ffprobe " +
                 "it does nothing — set the tools up on the External tools tab.")));

        panel.Children.Add(Group("Folders scanned in addition to whole drives",
            ScanFolderEditor(),
            Hint("A download folder, say. These are watched along with the drives, and are offered " +
                 "in the scan wizard so a folder can be rescanned on its own without re-walking a " +
                 "whole drive."),
            Hint("\nWhich drives to scan is chosen in the scan wizard — the Scan button on the " +
                 "toolbar — where the choice belongs with the run it applies to.")));

        return panel;
    }

    private StackPanel LibraryTab()
    {
        var panel = new StackPanel();

        panel.Children.Add(Group("Consolidation folders (one per category)",
            CategoryFolderEditor(_incoming),
            Hint(@"A consolidation folder is the central location scattered files are moved into, e.g. all TV under T:\TV\. " +
                 @"TV goes to <folder>\<A-Z or #>\<Show>\Season NN\ with episodes renamed ""01 - name.ext""; films to <folder>\<A-Z or #>\<Title (Year)>\. " +
                 "Specials and featurettes follow their show or film into an Extras subfolder."),
            Hint("\nA folder is checked when you add it: a drive that is not there is almost always " +
                 "a typo or an unplugged disk and is refused, and a folder on a drive that is there " +
                 "is simply created."),
            Hint("\nThe \"named\" box under each folder decides what the files in it are called — " +
                 "leave it empty for the built-in naming. Hover it for the list of fields; the " +
                 "example beside it shows what your pattern produces. The extension never changes, " +
                 "since nothing here re-encodes anything."),
            Hint("\nA file counts as filed only when it is at the exact place its title, year and " +
                 "numbering put it. Correct a title and its file stops being filed — consolidating " +
                 "it again moves it under the new name rather than reporting it as already done. " +
                 "When a whole folder is misnamed it is renamed in place rather than having its " +
                 "contents copied out one at a time."),
            Hint("\nConsolidating is always a move: a file already on the destination's drive is " +
                 "moved without being copied, and anything genuinely copied is verified against " +
                 "the original before the original is permanently deleted. One copy, in the " +
                 "library — including for TV, where an episode already there is never filed a " +
                 "second time under a different name.")));

        panel.Children.Add(Group("Subtitles",
            _consolidateSubtitles,
            Hint("A subtitle is tied to its film by name and by nothing else — \"The Film.mkv\" " +
                 "and \"The Film.eng.srt\" — so one left behind after the film has moved can never " +
                 "be matched to anything again. With this on they travel with the video and are " +
                 "renamed to match it; with it off they are deleted when the video is filed, so " +
                 "the source folder is left with nothing dead in it."),
            Hint("\nA rename always takes them along, whatever this says: renaming a film and " +
                 "leaving its subtitles under the old name would break them on the spot.")));

        panel.Children.Add(Group("What may be left behind",
            LeftoverThresholdEditor(_incoming),
            Hint("After a file is filed, its old folder often still holds something — a sample " +
                 "clip, a screenshot, a readme. Below this size that is scraps and the folder can " +
                 "go with it; above it, it is content and the folder stays."),
            Hint("\nThe figure is per category because the same three megabytes mean opposite " +
                 "things: left where a film used to be it is a sample, and in a music folder it is " +
                 "very probably a track. Leave a box empty for \"only when the folder is truly " +
                 "empty\", which is what every earlier version did."),
            Hint("\nOne thing overrides the size entirely: a catalogued file that has not been " +
                 "filed yet. However small it is, that is work you have not finished, and a folder " +
                 "holding one is never offered however far under the limit it falls."),
            Hint("\nA folder you have named anywhere in these settings — one you scan, one you " +
                 "watch, a consolidation folder — is never removed either, however empty it ends " +
                 "up. A download folder is empty most of the time; that is what it is for.")));

        panel.Children.Add(Group("How far two copies may disagree about their length",
            DurationToleranceEditor(_incoming),
            Hint("Two copies of one thing rarely run to the same second: one has the distributor's " +
                 "ident, the other has the credits trimmed. Within this many seconds they are " +
                 "treated as the same thing and consolidated by the ordinary rules; beyond it they " +
                 "are put to you, because at some point a longer file is a different cut rather " +
                 "than the same one."),
            Hint("\nThe figure is per category because a minute means opposite things in each. " +
                 "Sixty seconds between two rips of a film is the credits and nobody cares which " +
                 "copy has them; sixty seconds between two copies of a song is a different " +
                 "recording. Video starts at 60 seconds and audio at 2. Zero means the lengths " +
                 "have to match exactly."),
            Hint("\nWhen two copies do differ in length and are otherwise the same, the longer one " +
                 "is kept — provided it is not of worse quality. A copy with the credits on it is " +
                 "the more complete copy, and the shorter one has nothing the longer one lacks.")));

        panel.Children.Add(Group("Sorting",
            _articleLast,
            Hint("A library catalogue files \"The Simpsons\" under S, not T. With this on the show " +
                 "folder becomes ..\\S\\Simpsons (The)\\, which puts it beside *Seinfeld* rather " +
                 "than with every other programme whose name begins \"The\". Off by default: it " +
                 "moves existing folders the next time they are consolidated.")));

        return panel;
    }

    private StackPanel ExclusionsTab()
    {
        var panel = new StackPanel();

        panel.Children.Add(Group("Excluded folders",
            _excludeSystem,
            ExcludedEditor(),
            Hint(@"Paths may be exact folders (D:\Downloads) or patterns: * matches any run of characters, ? matches one. " +
                 @"So *\Windows\* excludes every Windows folder on every drive, and ?:\$Recycle.Bin excludes the bin on all of them.")));

        panel.Children.Add(Group("When a new rule covers older ones",
            Labeled("Redundant rules:", _redundantRules, 130),
            Hint(@"Excluding D:\Media makes an existing rule for D:\Media\Films pointless — the " +
                 "broader rule already covers it. Leaving both is harmless but clutters the list. " +
                 "Ask shows what has been superseded and lets you tick which of them to drop, all " +
                 "or some; Remove automatically drops the lot without stopping.")));

        panel.Children.Add(Group("Ignored file types (removed from results and skipped in future scans)",
            _extsBox,
            Hint("Separated by tabs — press Tab in the box — so twenty of them read across two " +
                 "lines rather than down a scrolling column. Spaces, commas and new lines are " +
                 "accepted too."),
            Hint("\nWildcards work: ? stands for exactly one character and * for any run of them. " +
                 "So \".mp?\" covers .mp3 and .mp4, and \".m*\" covers every extension beginning " +
                 "with m. The whole extension has to match, so \".mp3\" means .mp3 and not .mp3x " +
                 "as well.")));

        return panel;
    }

    private StackPanel CategoriesTab()
    {
        var panel = new StackPanel();

        panel.Children.Add(Group("Custom categories",
            ListEditor(_categories, addPrompt: "New category name", onAdd: AddCategory),
            Hint("Added to the built-in Movie / TvShow / TvExtra / MovieExtra / Audio / Other list " +
                 "wherever a category is chosen. Give a custom category a consolidation folder on " +
                 "the Library tab and its files are filed straight into it."),
            Hint("\nSeason and episode numbers belong to TvShow and TvExtra alone. Categorise a file " +
                 "as anything else and its numbering is cleared: it was read out of a number in the " +
                 "name that meant something else — the 13 in \"Apollo 13\", a track numbered 104.")));

        panel.Children.Add(Group("Order in the \"Set category\" menu",
            CategoryOrderEditor(),
            Hint("The order they appear in wherever a category is chosen. Somebody whose library is " +
                 "nine tenths television should not have to walk past Movie every time, so the " +
                 "order is yours. A category added later joins the bottom of the list rather than " +
                 "disappearing.")));

        return panel;
    }

    /// <summary>The menu order, with the two buttons that are the whole of the interaction.</summary>
    private FrameworkElement CategoryOrderEditor()
    {
        var wrap = new StackPanel();
        var list = new ListBox { Height = 160, ItemsSource = _categoryOrder };
        wrap.Children.Add(list);

        void Move(int delta)
        {
            var index = list.SelectedIndex;
            var target = index + delta;
            if (index < 0 || target < 0 || target >= _categoryOrder.Count) return;
            _categoryOrder.Move(index, target);
            list.SelectedIndex = target;
            list.ScrollIntoView(list.SelectedItem);
        }

        var controls = new StackPanel
        {
            Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0)
        };
        var up = new Button { Content = "Move up", Width = 90 };
        up.Click += (_, _) => Move(-1);
        controls.Children.Add(up);
        var down = new Button { Content = "Move down", Width = 90, Margin = new Thickness(6, 0, 0, 0) };
        down.Click += (_, _) => Move(+1);
        controls.Children.Add(down);

        var reset = new Button
        {
            Content = "Built-in order", Width = 110, Margin = new Thickness(6, 0, 0, 0),
            ToolTip = "Put them back the way they come out of the box."
        };
        reset.Click += (_, _) =>
        {
            var rebuilt = CategoryResolver.BuiltIn.Concat(_categories)
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            _categoryOrder.Clear();
            foreach (var c in rebuilt) _categoryOrder.Add(c);
        };
        controls.Children.Add(reset);

        wrap.Children.Add(controls);
        return wrap;
    }

    /// <summary>
    /// One box per category saying how little may be left in a folder before the folder
    /// itself is worth taking away. Extras are not listed: they follow the show or film they
    /// belong to, as their consolidation folder does.
    /// </summary>
    private FrameworkElement LeftoverThresholdEditor(AppSettings settings)
    {
        var wrap = new StackPanel();

        var categories = _knownCategories
            .Concat(settings.LeftoverThresholds.Select(t => t.Category))
            .Where(c => !string.IsNullOrWhiteSpace(c) && !CategoryResolver.IsExtra(c) &&
                        !string.Equals(c, CategoryResolver.Unknown, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var category in categories)
        {
            var box = new TextBox
            {
                Width = 90, VerticalContentAlignment = VerticalAlignment.Center,
                Text = FormatSize(settings.LeftoverThresholdFor(category))
            };
            _leftoverRows.Add((category, box));

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2)
            };
            row.Children.Add(new TextBlock
            {
                Text = category, Width = 130, VerticalAlignment = VerticalAlignment.Center
            });
            row.Children.Add(box);
            row.Children.Add(new TextBlock
            {
                Text = "left behind is scraps", VerticalAlignment = VerticalAlignment.Center,
                Foreground = System.Windows.Media.Brushes.Gray, Margin = new Thickness(8, 0, 0, 0)
            });
            wrap.Children.Add(row);
        }

        if (_leftoverRows.Count == 0)
            wrap.Children.Add(Hint("(no categories yet)"));

        return wrap;
    }

    /// <summary>
    /// One box per category saying how far two copies of one thing may disagree about their
    /// length and still be settled without asking. Extras are not listed: they follow the
    /// show or film they belong to, as everything else about them does.
    /// </summary>
    private FrameworkElement DurationToleranceEditor(AppSettings settings)
    {
        var wrap = new StackPanel();

        foreach (var category in ToleranceCategories(settings))
        {
            var box = new TextBox
            {
                Width = 70, VerticalContentAlignment = VerticalAlignment.Center,
                Text = settings.DurationToleranceFor(category).ToString()
            };
            _toleranceRows.Add((category, box));

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2)
            };
            row.Children.Add(new TextBlock
            {
                Text = category, Width = 130, VerticalAlignment = VerticalAlignment.Center
            });
            row.Children.Add(box);
            row.Children.Add(new TextBlock
            {
                Text = "seconds apart is still the same thing",
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = System.Windows.Media.Brushes.Gray, Margin = new Thickness(8, 0, 0, 0)
            });
            wrap.Children.Add(row);
        }

        if (_toleranceRows.Count == 0)
            wrap.Children.Add(Hint("(no categories yet)"));

        return wrap;
    }

    /// <summary>The categories both per-category editors offer, so the two lists agree.</summary>
    private IEnumerable<string> ToleranceCategories(AppSettings settings) =>
        _knownCategories
            .Concat(settings.DurationTolerances.Select(t => t.Category))
            .Where(c => !string.IsNullOrWhiteSpace(c) && !CategoryResolver.IsExtra(c) &&
                        !string.Equals(c, CategoryResolver.Unknown, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private StackPanel ToolsTab()
    {
        var panel = new StackPanel();

        var redetect = new Button
        {
            Content = "Re-detect", Width = 100, HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 8, 0, 8)
        };
        redetect.Click += (_, _) => ShowToolDetection();

        panel.Children.Add(Group("FFmpeg, ffprobe and fpcalc",
            Hint("The advanced features need external tools: fingerprinting, deep integrity checks, " +
                 "and reading each file's length and quality. Easiest option — drop ffmpeg.exe, " +
                 "ffprobe.exe and fpcalc.exe into a 'tools' folder next to this application and they " +
                 "are found automatically. Otherwise set explicit paths here; an empty box means " +
                 "auto-detect (PATH and the usual install folders)."),
            ToolRow("ffmpeg", _ffmpeg),
            ToolRow("ffprobe", _ffprobe),
            ToolRow("fpcalc", _fpcalc),
            redetect,
            _toolStatus,
            Hint("\nDownloads: FFmpeg → ffmpeg.org (the gyan.dev builds) provides ffmpeg.exe and " +
                 "ffprobe.exe. Chromaprint → acoustid.org/chromaprint provides fpcalc.exe. " +
                 "Both are free and portable — just unzip.")));

        ShowToolDetection();
        return panel;
    }

    private StackPanel DataTab()
    {
        var panel = new StackPanel();

        // The two sources take up a great deal of room between them and have nothing to do
        // with each other, so each gets its own box rather than sharing one long column.
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };

        Button? Downloader(string caption, string tip, Func<Task<string>>? job)
        {
            if (job == null) return null;
            var button = new Button
            {
                Content = caption, Width = 150, Padding = new Thickness(0, 3, 0, 3),
                Margin = new Thickness(0, 0, 8, 0), ToolTip = tip
            };
            button.Click += async (_, _) =>
            {
                button.IsEnabled = false;
                try
                {
                    var result = await job();
                    _imdbStatus.Text = ImdbStatusText();
                    MessageBox.Show(this, result, "IMDb data",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
                finally { button.IsEnabled = true; }
            };
            return button;
        }

        if (Downloader("Download titles", "Fetch title.basics.tsv.gz and extract it.", _downloadImdb)
            is { } titles) row.Children.Add(titles);
        if (Downloader("Download episodes",
                "Fetch title.episode.tsv.gz and re-extract, so a season's real length is known.",
                _downloadImdbEpisodes) is { } episodes) row.Children.Add(episodes);

        var reset = new Button
        {
            Content = "Use the default addresses", Width = 180,
            Padding = new Thickness(0, 3, 0, 3)
        };
        reset.Click += (_, _) =>
        {
            _imdbUrl.Text = AppSettings.DefaultImdbDownloadUrl;
            _imdbEpisodeUrl.Text = AppSettings.DefaultImdbEpisodeDownloadUrl;
        };
        row.Children.Add(reset);

        panel.Children.Add(Group("IMDb — the local data (no rate limit)",
            _useImdbFirst,
            _imdbInMemory,
            _imdbStatus,
            Labeled("Titles from:", _imdbUrl, 110),
            Labeled("Episodes from:", _imdbEpisodeUrl, 110),
            Hint("Where title.basics.tsv.gz and title.episode.tsv.gz are fetched from. The defaults " +
                 "are IMDb's own published addresses; they are settings so a changed address can be " +
                 "corrected here rather than waiting for a new version."),
            row,
            Hint("The titles download is around 150 MB. What is kept of it is the identifier, the " +
                 "title, the years, the type and the genres — with the type and the genres held as " +
                 "numbers explained by two small tables beside it, since \"tvEpisode\" written out " +
                 "on eight million rows is most of a gigabyte spent saying one word. The original " +
                 "title, the adult flag and the running time are dropped: the first repeats the " +
                 "primary title, the second is of no use here, and the third is better read from " +
                 "your own file than believed from a database."),
            Hint("\nThe episodes download says which episode of which programme each identifier is. " +
                 "It is optional, and it is what makes \"which episodes am I missing?\" a question " +
                 "with an answer: a folder holding episodes 1 to 12 looks complete from the inside, " +
                 "and only this can say the season ran to thirteen."),
            ExtractYearEditor()));

        panel.Children.Add(Group("themoviedb.org — the online fallback (deprecated)",
            Warning("Only used when IMDBData.tsv does not exist. TMDb answers one query every two " +
                    "seconds, so a library of any size spends hours there to reach an answer the " +
                    "local extract gives in a single pass — which makes \"use both\" a choice nobody " +
                    "would knowingly make. Once the extract is present TMDb is not consulted at all, " +
                    "and it is expected to be removed in a future release."),
            Labeled("API key (v3):", _apiKey, 110),
            Labeled("Read token (v4):", _readToken, 110),
            Hint("Get free credentials at themoviedb.org → account settings → API. Either the v4 " +
                 "Read Access Token or the v3 API Key works (the token is preferred).")));

        return panel;
    }

    // --- Layout helpers ---------------------------------------------------

    /// <summary>
    /// One titled box of related settings. Boxes rather than bold headings because these
    /// tabs have grown long enough that a heading alone no longer says where one group of
    /// options stops and the next begins.
    /// </summary>
    private static GroupBox Group(string header, params UIElement[] children)
    {
        var panel = new StackPanel();
        foreach (var child in children)
        {
            // Even spacing for the tick-boxes, without flattening an indent a caller has
            // deliberately given one ("…and start hidden" hanging off the option above it).
            if (child is CheckBox cb) cb.Margin = new Thickness(cb.Margin.Left, 3, 0, 3);
            panel.Children.Add(child);
        }

        return new GroupBox
        {
            Header = header,
            Margin = new Thickness(0, 0, 0, 10),
            Padding = new Thickness(8, 4, 8, 8),
            Content = panel
        };
    }

    /// <summary>A hint that is telling the user they are about to do something unwise.</summary>
    private static TextBlock Warning(string text) => new()
    {
        Text = text, Foreground = System.Windows.Media.Brushes.Firebrick,
        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(20, 2, 0, 4)
    };

    private static TextBlock Hint(string text) => new()
    {
        Text = text, Foreground = System.Windows.Media.Brushes.Gray,
        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0)
    };

    private static FrameworkElement Labeled(string label, FrameworkElement control, double labelWidth = 90)
    {
        var dp = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
        var t = new TextBlock { Text = label, Width = labelWidth, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(t, Dock.Left);
        dp.Children.Add(t);

        // A fixed-width control shouldn't be stretched across the row by the DockPanel.
        if (!double.IsNaN(control.Width))
        {
            control.HorizontalAlignment = HorizontalAlignment.Left;
            DockPanel.SetDock(control, Dock.Left);
        }
        dp.Children.Add(control);
        return dp;
    }

    private FrameworkElement ToolRow(string name, TextBox box)
    {
        var dp = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };

        var label = new TextBlock { Text = name, Width = 70, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(label, Dock.Left);
        dp.Children.Add(label);

        var browse = new Button { Content = "Browse…", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(6, 0, 0, 0) };
        browse.Click += (_, _) =>
        {
            var dlg = new OpenFileDialog
            {
                Title = $"Select {name}.exe",
                Filter = "Executables (*.exe)|*.exe|All files (*.*)|*.*"
            };
            if (dlg.ShowDialog(this) == true) { box.Text = dlg.FileName; ShowToolDetection(); }
        };
        DockPanel.SetDock(browse, Dock.Right);
        dp.Children.Add(browse);

        dp.Children.Add(box);
        return dp;
    }

    private void ShowToolDetection()
    {
        var resolved = ExternalTools.Resolve(CollectTools());
        string Line(string name, string? path) =>
            $"{name}: {(string.IsNullOrEmpty(path) ? "not found" : path)}";
        _toolStatus.Text =
            Line("ffmpeg", resolved.FfmpegPath) + "\n" +
            Line("ffprobe", resolved.FfprobePath) + "\n" +
            Line("fpcalc", resolved.FpcalcPath);
    }

    private ToolSettings CollectTools() => new()
    {
        FfmpegPath = _ffmpeg.Text.Trim(),
        FfprobePath = _ffprobe.Text.Trim(),
        FpcalcPath = _fpcalc.Text.Trim()
    };

    // --- Scan size limits -------------------------------------------------

    private FrameworkElement SizeLimitEditor()
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0)
        };
        row.Children.Add(new TextBlock
        {
            Text = "Size limits:", Width = 110, VerticalAlignment = VerticalAlignment.Center
        });
        row.Children.Add(new TextBlock
        {
            Text = "at least", VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        });
        row.Children.Add(_minSize);
        row.Children.Add(new TextBlock
        {
            Text = "at most", VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 6, 0)
        });
        row.Children.Add(_maxSize);

        var wrap = new StackPanel();
        wrap.Children.Add(row);
        wrap.Children.Add(Hint("Files outside these sizes are left out of the catalogue. Write a plain " +
                               "number of bytes or a size like 50MB, 1.5 GB, 700 KB. Leave either box " +
                               "empty for no limit — which is the default for both."));
        return wrap;
    }

    /// <summary>Show a byte count the way the user would type it back in.</summary>
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

    /// <summary>
    /// Read "50MB" / "1.5 GB" / "734003200" as a byte count. Returns null when the text
    /// makes no sense, so the caller can say so rather than silently reading it as zero.
    /// </summary>
    internal static long? ParseSize(string text)
    {
        var s = (text ?? string.Empty).Trim();
        if (s.Length == 0) return 0;   // empty means "no limit"

        var match = System.Text.RegularExpressions.Regex.Match(
            s, @"^(?<n>\d+(?:[.,]\d+)?)\s*(?<u>[KMGT]?B?)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success) return null;

        if (!double.TryParse(match.Groups["n"].Value.Replace(',', '.'),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var number))
            return null;

        var multiplier = match.Groups["u"].Value.ToUpperInvariant() switch
        {
            "KB" or "K" => 1024D,
            "MB" or "M" => 1024D * 1024,
            "GB" or "G" => 1024D * 1024 * 1024,
            "TB" or "T" => 1024D * 1024 * 1024 * 1024,
            _ => 1D
        };

        var bytes = number * multiplier;
        return bytes is >= 0 and < long.MaxValue ? (long)bytes : null;
    }

    /// <summary>
    /// The window of release years the extraction keeps. Most people are cataloguing what
    /// they own rather than what has ever been filmed, and every row left out is a row
    /// nothing has to read for the rest of the program's life.
    /// </summary>
    private FrameworkElement ExtractYearEditor()
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0)
        };
        row.Children.Add(new TextBlock
        {
            Text = "Years to keep:", Width = 110, VerticalAlignment = VerticalAlignment.Center
        });
        row.Children.Add(new TextBlock
        {
            Text = "from", VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        });
        row.Children.Add(_extractFrom);
        row.Children.Add(new TextBlock
        {
            Text = "to", VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 6, 0)
        });
        row.Children.Add(_extractTo);

        var wrap = new StackPanel();
        wrap.Children.Add(row);
        wrap.Children.Add(Hint("Titles released outside these years are left out of the extract. " +
                               "1950 to nothing in particular by default: the dataset reaches back to " +
                               "the 1890s, and hardly anybody is cataloguing that. Clear the first box " +
                               "to keep every year there is; leave the second empty for everything " +
                               "from the first year onwards, which is what most people want."));
        wrap.Children.Add(Hint("\nA title with no year at all is kept whichever way these are set: a " +
                               "missing date is not a date outside the range. Changing the years takes " +
                               "effect the next time the data is extracted — press Download titles, or " +
                               "delete IMDBData.tsv and verify titles again."));
        return wrap;
    }

    /// <summary>Where the IMDb data stands right now, so the options above make sense.</summary>
    private static string ImdbStatusText()
    {
        var extract = AppPaths.ImdbDataPath;
        var source = MediaCatalog.Core.Imdb.ImdbExtractor.FindSource(
            AppPaths.ImdbSourcePath, AppPaths.ImdbSourceGzPath);

        var episodes = File.Exists(AppPaths.ImdbEpisodesPath)
            ? $" Episode data is present ({FormatSize(new FileInfo(AppPaths.ImdbEpisodesPath).Length)}), " +
              "so a season can be checked against the number of episodes broadcast."
            : " There is no episode data, so a season can only be checked up to the highest " +
              "episode you hold.";

        if (!File.Exists(extract))
            return source != null
                ? $"{Path.GetFileName(source)} is present and will be extracted to IMDBData.tsv the " +
                  "first time titles are verified."
                : "There is no IMDb data yet. Download it below, or put title.basics.tsv.gz in " +
                  $"{AppPaths.DataDirectory} by hand — it is extracted automatically.";

        var format = MediaCatalog.Core.Imdb.ImdbExtractFormat.IsCurrentFormat(extract)
            ? ""
            : " It was written by an earlier version and carries no genres or episode links — " +
              "download the titles again to gain them.";

        return $"IMDBData.tsv is present ({FormatSize(new FileInfo(extract).Length)}).{format}{episodes}";
    }

    private FrameworkElement ListEditor(ObservableCollection<string> items, string addPrompt, Action<string> onAdd)
    {
        var dp = new DockPanel();
        var list = new ListBox { Height = 120, ItemsSource = items };

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
            Text = "Drives to watch (nothing ticked or listed below = every drive that was scanned):",
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

        wrap.Children.Add(new TextBlock
        {
            Text = "Particular folders to watch:",
            Margin = new Thickness(0, 10, 0, 2)
        });
        wrap.Children.Add(WatchFolderEditor());
        wrap.Children.Add(Hint("Watching E:\\dump\\ and watching the whole of E: are very different " +
                               "requests on a disk holding a hundred thousand files, and it is " +
                               "usually the first one that was meant. A folder listed here is " +
                               "watched along with anything ticked above, and subfolders come with " +
                               "it. Naming anything at all — a drive or a folder — means only what " +
                               "is named is watched."));
        return wrap;
    }

    private FrameworkElement WatchFolderEditor()
    {
        var wrap = new StackPanel();
        var list = new ListBox { Height = 80, ItemsSource = _watchFolders };
        wrap.Children.Add(list);

        var controls = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        var add = new Button { Content = "Add folder…", Width = 100 };
        add.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog { Title = "Choose a folder to watch for new files" };
            if (dlg.ShowDialog(this) == true &&
                !_watchFolders.Contains(dlg.FolderName, StringComparer.OrdinalIgnoreCase))
                _watchFolders.Add(dlg.FolderName);
        };
        controls.Children.Add(add);

        var remove = new Button { Content = "Remove", Width = 80, Margin = new Thickness(6, 0, 0, 0) };
        remove.Click += (_, _) => { if (list.SelectedItem is string s) _watchFolders.Remove(s); };
        controls.Children.Add(remove);
        wrap.Children.Add(controls);
        return wrap;
    }

    private FrameworkElement ScanFolderEditor()
    {
        var wrap = new StackPanel();
        var list = new ListBox { Height = 100, ItemsSource = _scanFolders };
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
        var list = new ListBox { Height = 140, ItemsSource = _excluded, DisplayMemberPath = nameof(ExclRow.Display) };
        wrap.Children.Add(list);

        var controls = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        var subdirs = new CheckBox { Content = "incl. subfolders", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        controls.Children.Add(subdirs);

        var browse = new Button { Content = "Add folder…", Width = 96, Margin = new Thickness(0, 0, 6, 0) };
        browse.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog { Title = "Choose folder to exclude" };
            if (dlg.ShowDialog(this) == true)
                AddExclusion(dlg.FolderName, subdirs.IsChecked == true);
        };
        controls.Children.Add(browse);

        // Typed rules are the only way to enter a pattern — a folder browser can't
        // produce "*\Windows\*", which matches many folders and exists as none.
        var addPath = new Button { Content = "Add path or pattern…", Width = 150, Margin = new Thickness(0, 0, 6, 0) };
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

        var edit = new Button { Content = "Edit…", Width = 70, Margin = new Thickness(0, 0, 6, 0) };
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
            PruneRedundant(r.Model);
        };
        controls.Children.Add(edit);

        var remove = new Button { Content = "Remove", Width = 80, Margin = new Thickness(0, 0, 6, 0) };
        remove.Click += (_, _) => { if (list.SelectedItem is ExclRow r) _excluded.Remove(r); };
        controls.Children.Add(remove);

        var tidy = new Button
        {
            Content = "Find redundant rules", Width = 150,
            ToolTip = "Look through the whole list for rules another rule already covers."
        };
        tidy.Click += (_, _) => TidyRedundant();
        controls.Children.Add(tidy);

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

    /// <summary>Add an exclusion, dealing with any rules it now supersedes.</summary>
    private void AddExclusion(string path, bool includeSubdirs)
    {
        if (!ConfirmPath(path)) return;

        var candidate = new ExcludedFolder { Path = path, IncludeSubdirectories = includeSubdirs };
        _excluded.Add(new ExclRow { Model = candidate });
        PruneRedundant(candidate);
    }

    /// <summary>The policy the user has chosen on this tab, which applies as they edit.</summary>
    private RedundantRuleAction Policy =>
        (RedundantRuleAction)Math.Max(0, _redundantRules.SelectedIndex);

    /// <summary>
    /// Apply the redundancy policy to a rule that has just been added or edited. The
    /// working list is what the user sees, so it is pruned in place.
    /// </summary>
    private void PruneRedundant(ExcludedFolder candidate)
    {
        var models = _excluded.Select(r => r.Model).ToList();
        var removed = AppSettings.PruneSuperseded(models, candidate, Policy, AskAboutRedundant);
        foreach (var rule in removed)
        {
            var row = _excluded.FirstOrDefault(r => ReferenceEquals(r.Model, rule));
            if (row != null) _excluded.Remove(row);
        }
    }

    /// <summary>Sweep the whole list for rules that another rule already covers.</summary>
    private void TidyRedundant()
    {
        var models = _excluded.Select(r => r.Model).ToList();
        var redundant = models
            .Where(inner => models.Any(outer => !ReferenceEquals(outer, inner) &&
                                                AppSettings.Supersedes(outer, inner)))
            .ToList();

        // The built-in system list counts too, when it is switched on.
        if (_excludeSystem.IsChecked == true)
        {
            var probe = new AppSettings { ExcludedFolders = models, ExcludeSystemDirectories = true };
            foreach (var rule in probe.FindCoveredBySystemRules())
                if (!redundant.Contains(rule)) redundant.Add(rule);
        }

        if (redundant.Count == 0)
        {
            MessageBox.Show(this, "Every rule in the list does something no other rule does.",
                "Redundant rules", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var chosen = Policy == RedundantRuleAction.RemoveAutomatically
            ? redundant
            : AskAboutRedundant(redundant).ToList();

        foreach (var rule in chosen)
        {
            var row = _excluded.FirstOrDefault(r => ReferenceEquals(r.Model, rule));
            if (row != null) _excluded.Remove(row);
        }
    }

    /// <summary>
    /// Put the superseded rules to the user one by one and hand back the ones they ticked.
    /// Removing some but not all is a perfectly reasonable answer, so this is a list rather
    /// than a yes or no.
    /// </summary>
    private IReadOnlyList<ExcludedFolder> AskAboutRedundant(IReadOnlyList<ExcludedFolder> superseded) =>
        RedundantRulesWindow.Ask(this, superseded);

    // --- Consolidation folders per category -------------------------------

    private FrameworkElement CategoryFolderEditor(AppSettings settings)
    {
        var wrap = new StackPanel();
        wrap.Children.Add(_catFolderPanel);

        foreach (var cf in settings.CategoryFolders)
            AddCategoryFolderRow(cf.Category, cf.Folder, cf.NameTemplate);
        if (_catFolders.Count == 0)
        {
            // A fresh install starts with the two categories everyone consolidates — and
            // with their name patterns filled in, because a pattern language is far easier
            // to learn from a working example than from a list of fields. Only on a fresh
            // install: filling the box in for a library that has already been filed under
            // the built-in naming would quietly rename everything the next time it was
            // consolidated, which is not a thing to do to somebody on their behalf.
            AddCategoryFolderRow("TvShow", settings.TvConsolidationDir, SuggestedTemplate("TvShow"));
            AddCategoryFolderRow("Movie", settings.FilmConsolidationDir, SuggestedTemplate("Movie"));
        }

        var add = new Button
        {
            Content = "Add category folder", Width = 150,
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0)
        };
        add.Click += (_, _) => AddCategoryFolderRow("", "", "");
        wrap.Children.Add(add);
        return wrap;
    }

    private void AddCategoryFolderRow(string category, string folder, string nameTemplate)
    {
        var rows = new StackPanel { Margin = new Thickness(0, 4, 0, 6) };
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

        // Checked as it is typed rather than only when Save is pressed, so a path with a
        // wrong drive letter in it is caught while the user is still looking at it.
        box.LostFocus += (_, _) => CheckConsolidationFolder(box.Text);

        // What the files in that folder are called. Empty means the built-in naming, which
        // is what every existing library has been filed under.
        var namePanel = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
        var nameLabel = new TextBlock
        {
            Text = "named", Width = 130, VerticalAlignment = VerticalAlignment.Center,
            Foreground = System.Windows.Media.Brushes.Gray,
            TextAlignment = TextAlignment.Right, Margin = new Thickness(0, 0, 6, 0)
        };
        DockPanel.SetDock(nameLabel, Dock.Left);
        namePanel.Children.Add(nameLabel);

        var nameBox = new TextBox
        {
            Text = nameTemplate, VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = NameTemplateTip()
        };
        var preview = new TextBlock
        {
            Width = 220, VerticalAlignment = VerticalAlignment.Center,
            Foreground = System.Windows.Media.Brushes.Gray,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(6, 0, 0, 0)
        };
        DockPanel.SetDock(preview, Dock.Right);
        namePanel.Children.Add(preview);

        // A worked example for this category, one click away. The pattern language is far
        // easier to learn by seeing a correct one than by reading the list of fields.
        var suggest = new Button
        {
            Content = "Suggest", Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(6, 0, 0, 0),
            ToolTip = "Fill the box in with a sensible pattern for this category, which you can " +
                      "then edit. Empty means the built-in naming."
        };
        suggest.Click += (_, _) => nameBox.Text = SuggestedTemplate(combo.Text.Trim());
        DockPanel.SetDock(suggest, Dock.Right);
        namePanel.Children.Add(suggest);

        namePanel.Children.Add(nameBox);

        void ShowPreview() => preview.Text = PreviewName(combo.Text.Trim(), nameBox.Text);
        nameBox.TextChanged += (_, _) => ShowPreview();
        ShowPreview();

        var row = new CatFolderRow
        {
            Category = combo, Folder = box, NameTemplate = nameBox, Container = rows
        };
        remove.Click += (_, _) =>
        {
            _catFolders.Remove(row);
            _catFolderPanel.Children.Remove(rows);
        };

        rows.Children.Add(dp);
        rows.Children.Add(namePanel);
        _catFolders.Add(row);
        _catFolderPanel.Children.Add(rows);
    }

    /// <summary>
    /// A working pattern for a category — what the "named" box is seeded with on a fresh
    /// install and what the Suggest button fills in.
    ///
    /// An episode leads with its number so a season folder sorts into broadcast order, and
    /// then says what it is; a film is its title and its year, which is how everybody writes
    /// a film. Extras follow whatever they are extras of.
    /// </summary>
    private static string SuggestedTemplate(string category) => category switch
    {
        CategoryResolver.TvShow or CategoryResolver.TvExtra => "{episode:00} - {title} - {numbering}",
        CategoryResolver.Movie or CategoryResolver.MovieExtra => "{title} ({year})",
        _ => "{title}"
    };

    /// <summary>Every field a name pattern may use, for the box's tooltip.</summary>
    private static string NameTemplateTip() =>
        "How consolidated files of this category are named. Empty = the built-in naming " +
        "(\"01 - original name.ext\" for an episode, the file's own name otherwise).\n\n" +
        "Fields:\n" +
        string.Join("\n", ConsolidationNaming.Fields.Select(f => $"    {f.Field} — {f.Means}")) +
        "\n\nA number can be padded: {episode:00}. The extension never changes — nothing here " +
        "re-encodes anything, so a name claiming otherwise would be lying.\n\n" +
        "Examples:\n" +
        "    {title} - {numbering}\n" +
        "    {episode:00} - {title} - {numbering}\n" +
        "    {title} ({year}) [{quality}]";

    /// <summary>
    /// What the pattern would call a file, shown beside the box. A pattern is far easier to
    /// get right when you can see what it produces.
    /// </summary>
    private static string PreviewName(string category, string template)
    {
        if (string.IsNullOrWhiteSpace(template)) return "built-in naming";

        var sample = string.Equals(category, "Movie", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(category, "MovieExtra", StringComparison.OrdinalIgnoreCase)
            ? new MediaFile
            {
                TmdbName = "Blade Runner", Year = 1982, Extension = ".mkv",
                FileName = "Blade.Runner.1982.1080p.BluRay.mkv", Kind = MediaKind.Video, Quality = 1080
            }
            : new MediaFile
            {
                TmdbName = "Burn Notice", Year = 2012, Season = 6, Episode = 11, EpisodeEnd = 12,
                Extension = ".mp4", FileName = "Burn.Notice.S06E11E12.HDTV.x264.mp4",
                Kind = MediaKind.Video, Quality = 720
            };

        var produced = ConsolidationNaming.Apply(sample, template);
        return produced.Length == 0 ? "⚠ produces no name at all" : "e.g. " + produced;
    }

    /// <summary>
    /// Check a consolidation folder and say so when it is wrong. A drive that is not there
    /// cannot be created and is nearly always a typo or an unplugged disk; a folder on a
    /// drive that *is* there is simply made, which is both the validation and the setup.
    /// Returns false only for a problem the user was told about and chose not to accept.
    /// </summary>
    private bool CheckConsolidationFolder(string folder, bool blocking = false)
    {
        var problem = AppSettings.ValidateConsolidationFolder(folder);
        if (problem == null) return true;

        if (!blocking)
        {
            MessageBox.Show(this, problem, "Consolidation folder",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        return MessageBox.Show(this,
            problem + "\n\nSave the setting anyway? Nothing can be consolidated there until " +
            "the folder exists.",
            "Consolidation folder", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }

    /// <summary>
    /// Categories offered in the row combos: the known ones plus any just added, minus the
    /// extras.
    ///
    /// TvExtra and MovieExtra are deliberately absent. A special belongs beside the film or
    /// the episode it is a special of, in an Extras subfolder of that — so a destination of
    /// its own is a setting that could only ever be ignored, and offering one is an invitation
    /// to configure something that does not exist.
    /// </summary>
    private List<string> CategoryChoices() =>
        _knownCategories.Concat(_categories)
            .Where(c => !string.IsNullOrWhiteSpace(c) && !CategoryResolver.IsExtra(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Read the ignored types out of the box. Tabs are what the box is written in, but
    /// spaces, commas and new lines are accepted too: nobody should have to think about
    /// which separator a list of file extensions wants.
    /// </summary>
    private List<string> ParseIgnoredExtensions() =>
        _extsBox.Text
            .Split(new[] { '\t', '\n', '\r', ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(e => e.Trim())
            .Where(e => e.Length > 0)
            .Select(e => (e.StartsWith('.') ? e : "." + e).ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private void AddCategory(string name)
    {
        if (_categories.Contains(name)) return;
        _categories.Add(name);

        // New categories join the bottom of the menu order rather than not appearing in it.
        if (!_categoryOrder.Contains(name, StringComparer.OrdinalIgnoreCase))
            _categoryOrder.Add(name);
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        // A size that can't be read is worth stopping for: silently treating it as "no
        // limit" would quietly catalogue everything the user meant to keep out.
        var min = ParseSize(_minSize.Text);
        var max = ParseSize(_maxSize.Text);
        if (min == null || max == null)
        {
            Complain("A size limit could not be read. Write a plain number of bytes, or a size " +
                     "like 50MB, 1.5 GB or 700 KB. Leave the box empty for no limit.", SettingsTab.Scanning);
            return;
        }
        if (min > 0 && max > 0 && min > max)
        {
            Complain("The smallest size is larger than the largest, so no file could ever match.",
                SettingsTab.Scanning);
            return;
        }

        var url = _imdbUrl.Text.Trim();
        var episodeUrl = _imdbEpisodeUrl.Text.Trim();
        foreach (var address in new[] { url, episodeUrl })
            if (address.Length > 0 &&
                (!Uri.TryCreate(address, UriKind.Absolute, out var parsed) ||
                 (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)))
            {
                Complain($"'{address}' is not an http:// or https:// address. Clear the box to fall " +
                         "back on the default, or press \"Use the default addresses\".",
                    SettingsTab.DataSources);
                return;
            }

        // The year window. Empty means "no limit at that end", which is a perfectly good
        // answer; anything else has to be a year, since a typo here would quietly halve the
        // data the whole program works from.
        if (!TryYear(_extractFrom.Text, out var extractFrom) ||
            !TryYear(_extractTo.Text, out var extractTo))
        {
            Complain("The years to keep must be whole years between 1800 and 2200. Leave a box " +
                     "empty for no limit at that end.", SettingsTab.DataSources);
            return;
        }
        if (extractFrom is { } first && extractTo is { } last && first > last)
        {
            Complain("The first year to keep is after the last, so no title could ever be kept.",
                SettingsTab.DataSources);
            return;
        }

        // The length tolerances, in seconds.
        var tolerances = new List<DurationTolerance>();
        foreach (var (category, box) in _toleranceRows)
        {
            var text = box.Text.Trim();
            var seconds = text.Length == 0 ? 0 : int.TryParse(text, out var parsed) ? parsed : -1;
            if (seconds is < 0 or > 86_400)
            {
                Complain($"The length tolerance for '{category}' must be a whole number of seconds " +
                         "from 0 to 86400. 0 means the lengths have to match exactly.",
                    SettingsTab.Library);
                return;
            }
            tolerances.Add(new DurationTolerance { Category = category, Seconds = seconds });
        }

        if (!int.TryParse(_notifyDelay.Text.Trim(), out var notifyDelay) ||
            notifyDelay is < 1 or > 600)
        {
            Complain("The wait before saying anything about new files must be between 1 and 600 " +
                     "seconds.", SettingsTab.General);
            return;
        }

        // Each leftover limit read the same way as a scan size limit, so "25MB" means the
        // same thing wherever a size is typed in this dialog.
        var leftovers = new List<LeftoverThreshold>();
        foreach (var (category, box) in _leftoverRows)
        {
            var bytes = ParseSize(box.Text);
            if (bytes == null)
            {
                Complain($"The leftover size for '{category}' could not be read. Write a plain " +
                         "number of bytes, or a size like 25MB. Leave the box empty for \"only " +
                         "when the folder is truly empty\".", SettingsTab.Library);
                return;
            }
            leftovers.Add(new LeftoverThreshold { Category = category, Bytes = bytes.Value });
        }

        var folders = new List<CategoryConsolidation>();
        foreach (var row in _catFolders)
        {
            var category = row.Category.Text.Trim();
            var folder = row.Folder.Text.Trim();
            if (category.Length == 0 || folder.Length == 0) continue;

            // A folder that cannot be reached is worth stopping for: consolidation is the
            // whole point of the setting, and it would fail silently on every file.
            if (!CheckConsolidationFolder(folder, blocking: true))
            {
                _tabs.SelectedIndex = (int)SettingsTab.Library;
                return;
            }

            // Last row wins if a category is listed twice.
            folders.RemoveAll(f => string.Equals(f.Category, category, StringComparison.OrdinalIgnoreCase));
            folders.Add(new CategoryConsolidation
            {
                Category = category, Folder = folder, NameTemplate = row.NameTemplate.Text.Trim()
            });
        }

        var result = new AppSettings
        {
            TmdbApiKey = _apiKey.Text.Trim(),
            TmdbReadAccessToken = _readToken.Text.Trim(),
            ImdbDownloadUrl = url,
            ImdbEpisodeDownloadUrl = episodeUrl,
            ExtractStartYear = extractFrom,
            ExtractEndYear = extractTo,
            StartWithWindows = _startup.IsChecked == true,
            StartInTray = _startInTray.IsChecked == true,
            AlwaysStartMinimised = _alwaysMinimised.IsChecked == true,
            MinimiseToTray = _minimiseToTray.IsChecked == true,
            WatchForNewFiles = _watch.IsChecked == true,
            MinFileSizeBytes = min.Value,
            MaxFileSizeBytes = max.Value,
            ScanMediaFilter = (ScanMediaFilter)(_scanFilter.SelectedItem ?? ScanMediaFilter.All),
            ProgressNamePosition = (ProgressNamePosition)(_progressName.SelectedItem ?? ProgressNamePosition.Left),
            UseImdbFirst = _useImdbFirst.IsChecked == true,
            ImdbInMemory = _imdbInMemory.IsChecked == true,
            RenameOnTitleChange = _renameOnTitle.IsChecked == true,
            SkipRecycleBinByDefault = _skipRecycleBin.IsChecked == true,
            OfferRemoveEmptyFolders = _offerEmptyFolders.IsChecked == true,
            DeleteEmptyFoldersPermanently = _deleteFoldersPermanently.IsChecked == true,
            ConsolidateSubtitles = _consolidateSubtitles.IsChecked == true,
            NewFileNotifyDelaySeconds = notifyDelay,
            RemoveEmptyFoldersAutomatically = _autoRemoveFolders.IsChecked == true,
            LeftoverThresholds = leftovers,
            LeftoverThresholdsInitialised = true,   // the user has now seen and set them
            DurationTolerances = tolerances,
            DurationTolerancesInitialised = true,
            CategoryOrder = _categoryOrder.ToList(),
            CapitaliseTitles = _capitaliseTitles.IsChecked == true,
            SortLeadingArticleLast = _articleLast.IsChecked == true,
            ProbeDuringScan = _probeDuringScan.IsChecked == true,
            DoubleClickAction = (DoubleClickAction)Math.Max(0, _doubleClick.SelectedIndex),
            RedundantExclusions = Policy,
            WatchedDrives = _driveChecks.Where(c => c.IsChecked == true)
                .Select(c => (string)c.Tag).ToList(),
            WatchedFolders = _watchFolders.ToList(),
            RememberFilters = _rememberFilters.IsChecked == true,
            ExcludeSystemDirectories = _excludeSystem.IsChecked == true,
            IgnoredExtensions = ParseIgnoredExtensions(),
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
            FolderTitleRules = _incoming.FolderTitleRules,
            ScanDrives = _incoming.ScanDrives,                   // owned by the scan wizard
            ScanWizardCompleted = _incoming.ScanWizardCompleted
        };
        result.SyncLegacyFolders();

        var tools = CollectTools();
        if (tools.FfmpegPath != _incomingTools.FfmpegPath ||
            tools.FfprobePath != _incomingTools.FfprobePath ||
            tools.FpcalcPath != _incomingTools.FpcalcPath)
            ToolsSaved?.Invoke(tools);

        Saved?.Invoke(result);
        Close();
    }

    /// <summary>
    /// Read a year, where an empty box means "no limit at this end" rather than a mistake.
    /// False only for text that is neither.
    /// </summary>
    private static bool TryYear(string text, out int? year)
    {
        year = null;
        var s = (text ?? string.Empty).Trim();
        if (s.Length == 0) return true;
        if (!int.TryParse(s, out var parsed) || parsed is < 1800 or > 2200) return false;
        year = parsed;
        return true;
    }

    /// <summary>Say what is wrong, on the tab where it can be put right.</summary>
    private void Complain(string message, SettingsTab tab)
    {
        _tabs.SelectedIndex = (int)tab;
        MessageBox.Show(this, message, "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
