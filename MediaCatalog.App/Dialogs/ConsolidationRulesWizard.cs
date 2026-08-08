using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MediaCatalog.Core.Consolidation;
using MediaCatalog.Core.Models;
using MediaCatalog.Core.Plugins;

namespace MediaCatalog.App;

/// <summary>
/// Everything a category's consolidation rules say, as the wizard collects them.
/// </summary>
public class ConsolidationRuleSet
{
    public DuplicateMatch MatchBy { get; set; } = DuplicateMatch.SameContentOrTitle;
    public List<ConsolidationRule> Rules { get; set; } = new();
    public bool DeepCheck { get; set; }
    public bool Fingerprint { get; set; }

    /// <summary>
    /// Rules written in the little comparison language, for a category whose question the
    /// ordered steps cannot put. Blank for every category that has not been given one — and
    /// when it is not blank it is what runs, the steps standing by untouched.
    /// </summary>
    public string Script { get; set; } = string.Empty;

    /// <summary>The rules in a line, for the row in Settings that opened this.</summary>
    public string Summarise()
    {
        if (!string.IsNullOrWhiteSpace(Script))
        {
            var lines = Script.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Count(l => !l.TrimStart().StartsWith('#'));
            return lines == 1 ? "one rule of your own" : $"{lines} rules of your own";
        }
        if (Rules.Count == 0) return "built-in judgement";
        var first = Rules[0].Describe().ToLowerInvariant();
        return Rules.Count == 1 ? first : $"{first}, then {Rules.Count - 1} more";
    }
}

/// <summary>What a deep check is known to have found, as the worked example lets you set it.</summary>
public enum KnownIntegrity
{
    /// <summary>Nothing has decoded it. Not the same as sound, and not the same as damaged.</summary>
    NotChecked = 0,
    Sound = 1,
    Damaged = 2
}

/// <summary>
/// One made-up copy in the wizard's worked example.
///
/// Every field here is one the rules can read, and that is the whole design of it: a
/// demonstration that only lets you change four of the ten things a rule can ask about is a
/// demonstration you cannot use to answer the question you actually have. The units are the
/// ones somebody would type — minutes rather than seconds, megabytes rather than bytes — and
/// the conversion happens on the way to the engine.
/// </summary>
public class SampleCopy : INotifyPropertyChanged
{
    private string _name = "";
    private double _minutes;
    private int _quality;
    private long _megabytes;
    private DateTime _modified = new(2020, 1, 1);
    private KnownIntegrity _known = KnownIntegrity.NotChecked;
    private bool _decodes = true;
    private bool _fingerprinted;
    private bool _filed;

    public string Name { get => _name; set { _name = value; Changed(nameof(Name)); } }
    public double Minutes { get => _minutes; set { _minutes = value; Changed(nameof(Minutes)); } }
    public int Quality { get => _quality; set { _quality = value; Changed(nameof(Quality)); } }
    public long Megabytes { get => _megabytes; set { _megabytes = value; Changed(nameof(Megabytes)); } }

    /// <summary>What the file system says. Read by a rule comparing Modified.</summary>
    public DateTime Modified { get => _modified; set { _modified = value; Changed(nameof(Modified)); } }

    /// <summary>What is known about it now — what Checked, Corrupt and DeepCheckIntegrity read.</summary>
    public KnownIntegrity Known { get => _known; set { _known = value; Changed(nameof(Known)); } }

    /// <summary>
    /// What a deep check would find if a rule asked for one. Kept apart from what is known,
    /// because the interesting question a script can ask is exactly the difference between
    /// them: what happens when the copy that wins on paper turns out not to decode.
    /// </summary>
    public bool Decodes { get => _decodes; set { _decodes = value; Changed(nameof(Decodes)); } }

    /// <summary>Whether it already has a fingerprint — what HasFingerprint reads.</summary>
    public bool Fingerprinted
    {
        get => _fingerprinted;
        set { _fingerprinted = value; Changed(nameof(Fingerprinted)); }
    }

    public bool Filed { get => _filed; set { _filed = value; Changed(nameof(Filed)); } }

    /// <summary>
    /// What a plugin would say about this file, by field name. Empty for every category no
    /// plugin handles, which is every built-in one.
    /// </summary>
    public Dictionary<string, string> Fields { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The sample as the rules engine sees it — the same object a real file is, so the answer
    /// below really is worked out by the code the real run uses.
    /// </summary>
    /// <param name="sameContent">
    /// Whether the copies in this example are the same thing. Fingerprints are made up to
    /// suit: identical when they are, opposite when they are not, so FingerprintsMatch and
    /// SameContent answer what the example says rather than what an empty string implies.
    /// </param>
    /// <param name="audio">Audio samples carry an acoustic fingerprint, video a visual one.</param>
    public MediaFile AsFile(bool sameContent, bool audio, int index)
    {
        var file = new MediaFile
        {
            FileName = Name,
            FullPath = @"D:\sample\" + Name,
            Extension = System.IO.Path.GetExtension(Name),
            Kind = audio ? MediaKind.Audio : MediaKind.Video,
            DurationSeconds = Minutes * 60,
            Quality = Quality,
            SizeBytes = Megabytes * 1024 * 1024,
            Integrity = Known switch
            {
                KnownIntegrity.Sound => IntegrityStatus.Ok,
                KnownIntegrity.Damaged => IntegrityStatus.Corrupt,
                _ => IntegrityStatus.NotChecked
            },
            Consolidated = Filed,
            LastModifiedUtc = Modified,
            PluginFields = Fields
                .Where(f => f.Value.Length > 0)
                .Select(f => new MediaFileField { Name = f.Key, Value = f.Value })
                .ToList()
        };

        if (Fingerprinted) Fingerprint(file, sameContent, audio, index);
        return file;
    }

    /// <summary>
    /// Give a sample a fingerprint that says what the example says. Two copies of one thing
    /// get the same one; two copies of different things get fingerprints as far apart as it
    /// is possible for two fingerprints to be.
    /// </summary>
    public static void Fingerprint(MediaFile file, bool sameContent, bool audio, int index)
    {
        // Every bit set, or every bit clear: the similarity between the two is 0, which is
        // as unlike as the matcher can score anything.
        var different = !sameContent && index > 0;

        if (audio)
            file.AudioFingerprint = string.Join(",",
                Enumerable.Repeat(different ? "4294967295" : "0", 32));
        else
            file.VideoFingerprint = string.Concat(
                Enumerable.Repeat(different ? "FFFFFFFFFFFFFFFF" : "0000000000000000", 16));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Changed(string property) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}

/// <summary>
/// Builds the steps that decide which copy of a thing a category keeps.
///
/// The rules are a small language, and a small language on its own is a puzzle: nobody can
/// tell from "keep the greater length, then the greater quality" what will happen to the two
/// files actually sitting on their disk. So the worked example is not a decoration — it is
/// the wizard. Sample copies sit at the bottom with every figure a rule can read, and every
/// edit to the rules says, in the same words the real run will use, which of them would be
/// kept and which step decided it.
///
/// The third tab is the rules the program uses when a category has none of its own, written
/// out in both of the ways the user can write theirs. Somebody about to write their first set
/// of rules should be able to read the ones they already have.
/// </summary>
public class ConsolidationRulesWizard : Window
{
    private readonly ObservableCollection<ConsolidationRule> _rules = new();
    private readonly ObservableCollection<SampleCopy> _samples = new();

    private readonly string _category;
    private readonly int _tolerance;
    private readonly bool _audio;
    private readonly IReadOnlyList<PluginField> _pluginFields;

    private readonly TabControl _how = new();
    private readonly ConsolidationScriptEditor _script = new();

    private readonly ListBox _ruleList = new() { Height = 140 };
    private readonly ComboBox _field = new() { Width = 190 };
    private readonly ComboBox _prefer = new() { Width = 150 };
    private readonly TextBox _tolerBox = new() { Width = 60, Text = "0" };
    private readonly ComboBox _match = new();
    private readonly CheckBox _sameThing = new()
    {
        Content = "These copies really are the same thing",
        IsChecked = true,
        ToolTip = "What a fingerprint comparison would find. Untick it to see what your rules " +
                  "do with two files that claim to be the same thing and are not."
    };
    private readonly CheckBox _deepCheck = new()
    {
        Content = "Decode every copy before choosing (slow, but it finds the damaged one)"
    };
    private readonly CheckBox _fingerprint = new()
    {
        Content = "Fingerprint every copy before choosing (confirms they really are the same thing)"
    };
    private readonly TextBlock _outcome = new()
    {
        TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.Bold,
        Margin = new Thickness(0, 6, 0, 0), MinHeight = 34
    };
    private readonly TextBlock _fieldHelp = new()
    {
        TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.Gray,
        Margin = new Thickness(0, 4, 0, 0), MinHeight = 32
    };

    /// <summary>What the user built, or null when they backed out.</summary>
    public ConsolidationRuleSet? Result { get; private set; }

    private readonly DuplicateMatch _incomingMatch;

    /// <param name="toleranceSeconds">
    /// How far apart two copies of one thing may run and still be the same thing, for this
    /// category. Only used to write the built-in rules out with the figure they would really
    /// use, which is the difference between showing somebody their rules and showing them a
    /// specimen.
    /// </param>
    public ConsolidationRulesWizard(
        string category, ConsolidationRuleSet existing, int toleranceSeconds = 60)
    {
        Title = $"Consolidation rules for {category}";
        Width = 980; Height = 820;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _category = category;
        _tolerance = Math.Max(0, toleranceSeconds);
        _audio = string.Equals(category, "Audio", StringComparison.OrdinalIgnoreCase);
        _pluginFields = MediaPlugins.FieldsFor(category);

        _incomingMatch = existing.MatchBy;
        foreach (var rule in existing.Rules) _rules.Add(Copy(rule));
        _deepCheck.IsChecked = existing.DeepCheck;
        _fingerprint.IsChecked = existing.Fingerprint;
        _script.Script = existing.Script;

        SeedSamples();

        var root = new DockPanel { Margin = new Thickness(14) };
        var buttons = Buttons();
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        var body = new StackPanel();
        body.Children.Add(Intro(category));
        body.Children.Add(MatchingBox());
        body.Children.Add(HowBox());
        body.Children.Add(WorkBox());
        body.Children.Add(SampleBox());

        root.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = body
        });

        Content = root;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };

        Recalculate();
    }

    private static ConsolidationRule Copy(ConsolidationRule rule) => new()
    {
        Field = rule.Field, FieldName = rule.FieldName,
        Prefer = rule.Prefer, Tolerance = rule.Tolerance
    };

    private static TextBlock Intro(string category) => new()
    {
        Text = $"When two files both claim to be the same {category.ToLowerInvariant()}, only one " +
               "of them belongs in the library. These steps decide which — in order, the first " +
               "one that can tell the copies apart having the final say. With no steps at all " +
               "the built-in judgement applies, and the third tab below is exactly what that " +
               "judgement says, in the same two forms your own rules are written in.",
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 0, 0, 10)
    };

    private GroupBox MatchingBox()
    {
        _match.ItemsSource = Enum.GetValues(typeof(DuplicateMatch));
        _match.SelectedItem = _incomingMatch;
        _match.Width = 320;
        _match.HorizontalAlignment = HorizontalAlignment.Left;

        var explanation = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = System.Windows.Media.Brushes.Gray,
            Margin = new Thickness(0, 4, 0, 0)
        };
        void Explain() => explanation.Text = _match.SelectedItem is DuplicateMatch m
            ? ConsolidationRules.Describe(m)
            : string.Empty;
        _match.SelectionChanged += (_, _) => Explain();
        Explain();

        var panel = new StackPanel();
        panel.Children.Add(_match);
        panel.Children.Add(explanation);

        return new GroupBox
        {
            Header = "What counts as two copies of one thing",
            Margin = new Thickness(0, 0, 0, 10),
            Padding = new Thickness(8),
            Content = panel
        };
    }

    /// <summary>
    /// The ways of saying which copy to keep, side by side.
    ///
    /// The steps are the answer for almost everybody: an ordered list, first one that can tell
    /// the copies apart wins, and no way to write it wrong. What they cannot say is anything
    /// conditional — "the better picture, unless it fails a decode", "the longer one, but only
    /// when they are more than a minute apart" — because a step compares one thing and knows
    /// nothing about any other. That is what the second tab is for, and it is why it is a tab
    /// rather than a replacement: reaching for it should be a decision, not the default.
    ///
    /// The third is read-only and is the program's own rules. It is there because the best
    /// starting point for a set of rules is a working set of rules.
    /// </summary>
    private GroupBox HowBox()
    {
        var steps = new TabItem { Header = "Steps, in order", Content = StepsPanel() };
        var script = new TabItem { Header = "Rules of your own", Content = ScriptPanel() };
        var builtIn = new TabItem { Header = "What the built-in rules do", Content = BuiltInPanel() };

        _how.Items.Add(steps);
        _how.Items.Add(script);
        _how.Items.Add(builtIn);
        _how.SelectedItem = string.IsNullOrWhiteSpace(_script.Script) ? steps : script;
        _how.SelectionChanged += (_, e) =>
        {
            if (e.OriginalSource == _how) Recalculate();
        };

        return new GroupBox
        {
            Header = "How to choose",
            Margin = new Thickness(0, 0, 0, 10),
            Padding = new Thickness(8),
            Content = _how
        };
    }

    /// <summary>Which tab is in front, and so which set of rules the worked example runs.</summary>
    private bool UsingScript => _how.SelectedIndex == 1;
    private bool ShowingBuiltIn => _how.SelectedIndex == 2;

    private FrameworkElement ScriptPanel()
    {
        _script.Changed += Recalculate;

        var panel = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        panel.Children.Add(new TextBlock
        {
            Text = "Rules for the questions the steps cannot put. Two files are compared at a " +
                   "time — File1 is the copy that has won so far, File2 the next one — and the " +
                   "first rule that names a copy ends it. With more than two different copies " +
                   "of one thing, the winner goes on to meet the next, until one is left. " +
                   "Only unique copies are ever compared, and no file is decoded, fingerprinted " +
                   "or measured twice however many rounds it survives.",
            TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.Gray,
            Margin = new Thickness(0, 0, 0, 8)
        });
        panel.Children.Add(_script);
        return panel;
    }

    // --- The built-in rules, shown -------------------------------------------

    /// <summary>
    /// The judgement the program makes for itself, written down twice: as steps, and in the
    /// language. Read-only, with a button to copy either into the tabs beside it.
    ///
    /// Two forms rather than one because they are not the same thing and the difference is
    /// the lesson. The steps are the choosing; the rules are the choosing plus the two places
    /// the program refuses to choose. Anyone who copies the steps and wonders why their
    /// library files more readily than it used to has the answer in front of them.
    /// </summary>
    private FrameworkElement BuiltInPanel()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };

        panel.Children.Add(new TextBlock
        {
            Text = BuiltInRules.Explain(_category, _tolerance),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
            MaxHeight = 150
        });

        var columns = new Grid();
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.35, GridUnitType.Star) });

        // Left: the same thing as steps.
        var stepsSide = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
        stepsSide.Children.Add(new TextBlock
        {
            Text = "As steps", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 4)
        });
        stepsSide.Children.Add(new ListBox
        {
            ItemsSource = BuiltInRules.Steps().Select(r => r.Describe()).ToList(),
            Height = 96, IsHitTestVisible = false,
            Background = System.Windows.Media.Brushes.WhiteSmoke
        });
        stepsSide.Children.Add(new TextBlock
        {
            Text = BuiltInRules.StepsFallShort,
            TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.Gray,
            Margin = new Thickness(0, 4, 0, 6)
        });
        stepsSide.Children.Add(Small("Copy these steps into mine", () =>
        {
            _rules.Clear();
            foreach (var rule in BuiltInRules.Steps()) _rules.Add(rule);
            _how.SelectedIndex = 0;
            Recalculate();
        }));
        Grid.SetColumn(stepsSide, 0);
        columns.Children.Add(stepsSide);

        // Right: the same thing in the language.
        var scriptSide = new StackPanel();
        scriptSide.Children.Add(new TextBlock
        {
            Text = "As rules of your own", FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 4)
        });
        scriptSide.Children.Add(new TextBox
        {
            Text = BuiltInRules.Script(_tolerance),
            IsReadOnly = true, Height = 96,
            FontFamily = new System.Windows.Media.FontFamily("Consolas, Courier New, monospace"),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            TextWrapping = TextWrapping.NoWrap,
            Background = System.Windows.Media.Brushes.WhiteSmoke
        });
        scriptSide.Children.Add(new TextBlock
        {
            Text = BuiltInRules.Differences,
            TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.Gray,
            Margin = new Thickness(0, 4, 0, 6)
        });
        scriptSide.Children.Add(Small("Copy these rules into mine", () =>
        {
            _script.Script = BuiltInRules.Script(_tolerance);
            _how.SelectedIndex = 1;
            Recalculate();
        }));
        Grid.SetColumn(scriptSide, 1);
        columns.Children.Add(scriptSide);

        panel.Children.Add(columns);
        return panel;
    }

    private FrameworkElement StepsPanel()
    {
        // A rule writes itself out as its own sentence, so the list needs nothing but the
        // rules themselves.
        _ruleList.ItemsSource = _rules;

        var editor = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };

        _field.ItemsSource = FieldChoices();
        _field.DisplayMemberPath = nameof(FieldChoice.Caption);
        _field.SelectedIndex = 1;   // Quality, as it always has been
        _field.SelectionChanged += (_, _) => ShowFieldHelp();

        _prefer.ItemsSource = new[] { "Keep the greater", "Keep the lesser" };
        _prefer.SelectedIndex = 0;

        var add = new Button
        {
            Content = "Add step", Padding = new Thickness(10, 2, 10, 2),
            Margin = new Thickness(8, 0, 0, 0)
        };
        add.Click += (_, _) => AddStep();

        editor.Children.Add(new TextBlock
        {
            Text = "Compare:", VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        });
        editor.Children.Add(_field);
        editor.Children.Add(new TextBlock
        {
            Text = " and ", VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 6, 0)
        });
        editor.Children.Add(_prefer);
        editor.Children.Add(new TextBlock
        {
            Text = " ignoring differences under ", VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 6, 0)
        });
        editor.Children.Add(_tolerBox);
        editor.Children.Add(add);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        buttons.Children.Add(SmallButton("Move up", () => Move(-1)));
        buttons.Children.Add(SmallButton("Move down", () => Move(+1)));
        buttons.Children.Add(SmallButton("Remove", RemoveStep));
        buttons.Children.Add(SmallButton("Clear all", () => { _rules.Clear(); Recalculate(); }));

        var panel = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        panel.Children.Add(_ruleList);
        panel.Children.Add(buttons);
        panel.Children.Add(editor);
        panel.Children.Add(_fieldHelp);
        ShowFieldHelp();

        return panel;
    }

    /// <summary>Something a step can compare: one of the built-in seven, or a plugin's field.</summary>
    private sealed record FieldChoice(string Caption, ConsolidationField Field, string PluginField, string Meaning)
    {
        public bool IsPlugin => PluginField.Length > 0;
    }

    /// <summary>
    /// What the Compare dropdown offers: everything every file has, then whatever a plugin
    /// has added for this category. A step about an author is only offered where files can
    /// have one, which is what stops the dropdown filling up with fields that mean nothing
    /// for the category being configured.
    /// </summary>
    private List<FieldChoice> FieldChoices()
    {
        var choices = Enum.GetValues<ConsolidationField>()
            .Select(f => new FieldChoice(
                ConsolidationRule.Label(f), f, string.Empty, ConsolidationRule.Explain(f)))
            .ToList();

        choices.AddRange(_pluginFields.Select(f => new FieldChoice(
            f.Label, ConsolidationField.Size, f.Name,
            $"{f.Meaning} From the {f.PluginName} plugin, which is what knows about " +
            $"'{f.MediaType}' files.".TrimStart())));

        return choices;
    }

    private GroupBox WorkBox()
    {
        var panel = new StackPanel();
        panel.Children.Add(_fingerprint);
        panel.Children.Add(_deepCheck);
        panel.Children.Add(new TextBlock
        {
            Text = "Both cost real time — a decode is minutes a file — so they are worth asking " +
                   "for only when the steps above need what they find. A step that compares " +
                   "length or quality already measures what it needs on its own.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = System.Windows.Media.Brushes.Gray,
            Margin = new Thickness(0, 4, 0, 0)
        });

        return new GroupBox
        {
            Header = "Before the steps run",
            Margin = new Thickness(0, 0, 0, 10),
            Padding = new Thickness(8),
            Content = panel
        };
    }

    /// <summary>
    /// The worked example: made-up copies, every figure a rule can read, and the answer.
    ///
    /// Every column here is something the rules can ask about, and that is the point of
    /// having them all. A demonstration that shows four of the ten fields is a demonstration
    /// that can answer four tenths of the questions somebody has, and the ones it cannot
    /// answer are exactly the ones worth asking — what happens when the best copy is the one
    /// that will not decode, what happens when nothing has fingerprinted either of them.
    /// </summary>
    private GroupBox SampleBox()
    {
        var grid = new DataGrid
        {
            ItemsSource = _samples,
            AutoGenerateColumns = false,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            MaxHeight = 150
        };

        grid.Columns.Add(Text("Name", nameof(SampleCopy.Name), 190,
            "The file's name. Its length is what NameLength compares."));
        grid.Columns.Add(Text("Minutes", nameof(SampleCopy.Minutes), 62, "How long it runs."));
        grid.Columns.Add(Text(_audio ? "kbps" : "Quality", nameof(SampleCopy.Quality), 60,
            "Picture height for video, bitrate for audio."));
        grid.Columns.Add(Text("MB", nameof(SampleCopy.Megabytes), 60, "Size on disk."));
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Modified",
            Binding = new System.Windows.Data.Binding(nameof(SampleCopy.Modified))
            {
                StringFormat = "yyyy-MM-dd"
            },
            Width = 88
        });

        grid.Columns.Add(new DataGridComboBoxColumn
        {
            Header = "Known",
            ItemsSource = Enum.GetValues(typeof(KnownIntegrity)),
            SelectedItemBinding = new System.Windows.Data.Binding(nameof(SampleCopy.Known)),
            Width = 88
        });
        grid.Columns.Add(Tick("Decodes", nameof(SampleCopy.Decodes), 62));
        grid.Columns.Add(Tick("Printed", nameof(SampleCopy.Fingerprinted), 58));
        grid.Columns.Add(Tick("Filed", nameof(SampleCopy.Filed), 48));

        // Whatever a plugin says a file of this category has.
        foreach (var field in _pluginFields)
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = field.Label,
                Binding = new System.Windows.Data.Binding($"Fields[{field.Name}]"),
                Width = 110
            });

        // Recalculate the moment an edit is committed, which is what makes this a
        // demonstration rather than a table.
        grid.CellEditEnding += (_, _) => Dispatcher.BeginInvoke(new Action(Recalculate),
            System.Windows.Threading.DispatcherPriority.Background);
        foreach (var sample in _samples) Watch(sample);

        _sameThing.Checked += (_, _) => Recalculate();
        _sameThing.Unchecked += (_, _) => Recalculate();

        var controls = new StackPanel
        {
            Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0)
        };
        controls.Children.Add(SmallButton("Add a copy", AddSample));
        controls.Children.Add(SmallButton("Remove the last", RemoveSample));
        controls.Children.Add(_sameThing);
        _sameThing.VerticalAlignment = VerticalAlignment.Center;
        _sameThing.Margin = new Thickness(8, 0, 0, 0);

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = "Copies of one thing, made up. Every column is something a rule can read. " +
                   "Change any of them and the answer below changes with it — it is worked out " +
                   "by the same code the real run uses. \"Known\" is what has been found out " +
                   "about a copy already; \"Decodes\" is what a deep check would find if a rule " +
                   "asked for one, which is not the same question.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = System.Windows.Media.Brushes.Gray,
            Margin = new Thickness(0, 0, 0, 6)
        });
        panel.Children.Add(grid);
        panel.Children.Add(controls);
        panel.Children.Add(_outcome);

        return new GroupBox
        {
            Header = "What would happen",
            Margin = new Thickness(0, 0, 0, 10),
            Padding = new Thickness(8),
            Content = panel
        };
    }

    private static DataGridTextColumn Text(string header, string path, double width, string tip) => new()
    {
        Header = header,
        Binding = new System.Windows.Data.Binding(path),
        Width = width,
        HeaderStyle = Tip(tip)
    };

    private static DataGridCheckBoxColumn Tick(string header, string path, double width) => new()
    {
        Header = header,
        Binding = new System.Windows.Data.Binding(path) { UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged },
        Width = width
    };

    private static Style Tip(string text)
    {
        var style = new Style(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader));
        style.Setters.Add(new Setter(ToolTipProperty, text));
        return style;
    }

    private void Watch(SampleCopy sample) => sample.PropertyChanged += (_, _) => Recalculate();

    private StackPanel Buttons()
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0)
        };

        var ok = new Button
        {
            Content = "Use these rules", Width = 130, IsDefault = true,
            FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 6, 0)
        };
        ok.Click += (_, _) =>
        {
            // Rules that do not read are not saved. Silently keeping a script the run will
            // refuse means a consolidation that quietly does nothing a week from now, with
            // nothing on screen to say why.
            if (UsingScript && _script.Problem is { Length: > 0 } problem)
            {
                MessageBox.Show(this, problem, "The rules do not read",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Result = new ConsolidationRuleSet
            {
                MatchBy = _match.SelectedItem is DuplicateMatch m ? m : DuplicateMatch.SameContentOrTitle,
                Rules = _rules.Select(Copy).ToList(),
                DeepCheck = _deepCheck.IsChecked == true,
                Fingerprint = _fingerprint.IsChecked == true,
                // The steps and the script are both kept; only the tab in front decides which
                // of them runs, so backing out of a script leaves the steps where they were.
                // Sitting on the built-in tab changes nothing: it is there to be read.
                Script = UsingScript ? _script.Script.Trim() : string.Empty
            };
            DialogResult = true;
        };
        panel.Children.Add(ok);

        var cancel = new Button { Content = "Cancel", Width = 90, IsCancel = true };
        panel.Children.Add(cancel);
        return panel;
    }

    private Button SmallButton(string caption, Action onClick) => Small(caption, onClick);

    private Button Small(string caption, Action onClick)
    {
        var button = new Button
        {
            Content = caption, Padding = new Thickness(10, 2, 10, 2),
            Margin = new Thickness(0, 0, 6, 0),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    private void AddStep()
    {
        if (_field.SelectedItem is not FieldChoice choice) return;
        double.TryParse(_tolerBox.Text, out var tolerance);

        _rules.Add(new ConsolidationRule
        {
            Field = choice.Field,
            FieldName = choice.PluginField,
            Prefer = _prefer.SelectedIndex == 1 ? RulePreference.Lesser : RulePreference.Greater,
            Tolerance = Math.Max(0, tolerance)
        });
        Recalculate();
    }

    private void RemoveStep()
    {
        if (_ruleList.SelectedItem is ConsolidationRule rule) _rules.Remove(rule);
        Recalculate();
    }

    private void Move(int by)
    {
        var index = _ruleList.SelectedIndex;
        var to = index + by;
        if (index < 0 || to < 0 || to >= _rules.Count) return;
        _rules.Move(index, to);
        _ruleList.SelectedIndex = to;
        Recalculate();
    }

    private void ShowFieldHelp() =>
        _fieldHelp.Text = _field.SelectedItem is FieldChoice choice ? choice.Meaning : string.Empty;

    /// <summary>
    /// Sample copies worth arguing about: the same thing twice, one a better picture and the
    /// other longer, which is exactly the case the built-in rules hand to the user.
    /// </summary>
    private void SeedSamples()
    {
        _samples.Add(Blank(new SampleCopy
        {
            Name = _audio ? "Track (album version).flac" : "The Film (1080p).mkv",
            Minutes = _audio ? 5.2 : 118,
            Quality = _audio ? 320 : 1080,
            Megabytes = _audio ? 42 : 4300,
            Modified = new DateTime(2021, 3, 14),
            Known = KnownIntegrity.NotChecked,
            Decodes = true,
            Filed = false
        }));
        _samples.Add(Blank(new SampleCopy
        {
            Name = _audio ? "Track (single edit).mp3" : "The Film (720p, extended).mkv",
            Minutes = _audio ? 3.8 : 131,
            Quality = _audio ? 192 : 720,
            Megabytes = _audio ? 9 : 2100,
            Modified = new DateTime(2019, 11, 2),
            Known = KnownIntegrity.NotChecked,
            Decodes = true,
            Filed = true
        }));
    }

    /// <summary>Give a sample an empty box for every field a plugin says its kind has.</summary>
    private SampleCopy Blank(SampleCopy sample)
    {
        foreach (var field in _pluginFields)
            sample.Fields[field.Name] = string.Empty;
        return sample;
    }

    private void AddSample()
    {
        var sample = Blank(new SampleCopy
        {
            Name = $"Another copy ({_samples.Count + 1}){(_audio ? ".mp3" : ".mkv")}",
            Minutes = _samples.Count > 0 ? _samples[0].Minutes : 100,
            Quality = _samples.Count > 0 ? _samples[0].Quality : 720,
            Megabytes = _samples.Count > 0 ? _samples[0].Megabytes : 1000,
            Modified = new DateTime(2022, 6, 1)
        });
        Watch(sample);
        _samples.Add(sample);
        Recalculate();
    }

    private void RemoveSample()
    {
        // Two is the fewest that is a question at all: one copy is kept because it is the
        // only one, and no rule ever runs.
        if (_samples.Count <= 2) return;
        _samples.RemoveAt(_samples.Count - 1);
        Recalculate();
    }

    /// <summary>
    /// The same worked example for rules written in the language: the script is run over the
    /// made-up copies by the very code a consolidation would use.
    ///
    /// What it cannot do is decode or fingerprint a file that does not exist, so those are
    /// answered from the sample's own figures and counted. Saying "it would have decoded both
    /// of these" is the useful half of that answer anyway — a script nobody has priced is how
    /// an overnight consolidation turns into a week of decoding.
    /// </summary>
    private void RecalculateScript(List<MediaFile> copies, string script, string nothingYet)
    {
        if (string.IsNullOrWhiteSpace(script))
        {
            _outcome.Text = nothingYet;
            _outcome.Foreground = System.Windows.Media.Brushes.DimGray;
            return;
        }

        if (!RuleScriptParser.TryParse(script, out var program, out var error))
        {
            _outcome.Text = $"These rules do not read: {error}";
            _outcome.Foreground = System.Windows.Media.Brushes.Firebrick;
            return;
        }

        var pretend = new PretendServices(_samples.ToList(), copies, SameThing, _audio);
        var session = new RuleScriptSession(program, pretend);
        var verdict = session.ChooseAsync(copies).GetAwaiter().GetResult();

        Say(copies, verdict, pretend.Describe());
    }

    private bool SameThing => _sameThing.IsChecked == true;

    /// <summary>
    /// Stands in for the external tools while the example is being worked.
    ///
    /// Nothing is measured — there is nothing on disk to measure — but everything is answered
    /// from what the sample rows say: a decode finds what the "Decodes" box says it would find,
    /// a fingerprint gives a file one, and whether the two are the same content is the tick
    /// box under the table. What all of that would have cost is counted, so the wizard can
    /// say what the rules are going to be worth in hours.
    /// </summary>
    private sealed class PretendServices : IRuleScriptServices
    {
        private readonly List<SampleCopy> _rows;
        private readonly List<MediaFile> _files;
        private readonly bool _same;
        private readonly bool _audio;
        private readonly HashSet<string> _scanned = new();
        private readonly HashSet<string> _fingerprinted = new();

        public PretendServices(List<SampleCopy> rows, List<MediaFile> files, bool same, bool audio)
        {
            _rows = rows;
            _files = files;
            _same = same;
            _audio = audio;
        }

        public Task ProbeAsync(MediaFile file, CancellationToken ct) => Task.CompletedTask;

        public Task DeepScanAsync(MediaFile file, CancellationToken ct)
        {
            _scanned.Add(file.FullPath);
            if (Row(file) is { } row)
                file.Integrity = row.Decodes ? IntegrityStatus.Ok : IntegrityStatus.Corrupt;
            return Task.CompletedTask;
        }

        public Task FingerprintAsync(MediaFile file, CancellationToken ct)
        {
            _fingerprinted.Add(file.FullPath);
            SampleCopy.Fingerprint(file, _same, _audio, _files.IndexOf(file));
            return Task.CompletedTask;
        }

        private SampleCopy? Row(MediaFile file)
        {
            var index = _files.IndexOf(file);
            return index >= 0 && index < _rows.Count ? _rows[index] : null;
        }

        public string Describe()
        {
            var parts = new List<string>();
            if (_scanned.Count > 0) parts.Add($"{_scanned.Count} file(s) decoded end to end");
            if (_fingerprinted.Count > 0) parts.Add($"{_fingerprinted.Count} file(s) fingerprinted");
            return parts.Count == 0
                ? ""
                : $" Getting there would have meant {string.Join(" and ", parts)}.";
        }
    }

    /// <summary>Run the rules over the samples and say, in words, what they decided.</summary>
    private void Recalculate()
    {
        var copies = _samples
            .Select((s, i) => s.AsFile(SameThing, _audio, i))
            .ToList();

        if (ShowingBuiltIn)
        {
            RecalculateScript(copies, BuiltInRules.Script(_tolerance), string.Empty);
            return;
        }

        if (UsingScript)
        {
            RecalculateScript(copies, _script.Script,
                "No rules of your own yet. Drag some pieces in above, or take a copy of the " +
                "built-in ones from the tab beside this and change what they say.");
            return;
        }

        if (_rules.Count == 0)
        {
            _outcome.Text = "With no steps, the built-in judgement decides — and the tab beside " +
                            "this one says, in full, what that judgement is.";
            _outcome.Foreground = System.Windows.Media.Brushes.DimGray;
            return;
        }

        Say(copies, ConsolidationRules.Choose(copies, _rules.ToList()), string.Empty);
    }

    /// <summary>The verdict in the words the real run would use.</summary>
    private void Say(List<MediaFile> copies, RuleVerdict verdict, string cost)
    {
        if (verdict.Winner is not { } keeper)
        {
            _outcome.Text = $"Nothing would be filed: {verdict.Why}. These copies would be " +
                            "put to you to choose between." + cost;
            _outcome.Foreground = System.Windows.Media.Brushes.Firebrick;
            return;
        }

        var index = copies.IndexOf(keeper);
        var name = index >= 0 && index < _samples.Count ? _samples[index].Name : keeper.FileName;
        var doomed = copies.Where(c => !ReferenceEquals(c, keeper)).Select(c => c.FileName);

        _outcome.Text = $"\"{name}\" would be filed — {verdict.Why}. " +
                        $"{string.Join(", ", doomed)} would be deleted once it is." + cost;
        _outcome.Foreground = System.Windows.Media.Brushes.DarkGreen;
    }
}
