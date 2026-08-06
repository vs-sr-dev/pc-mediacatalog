using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MediaCatalog.Core.Consolidation;
using MediaCatalog.Core.Models;

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

    /// <summary>The rules in a line, for the row in Settings that opened this.</summary>
    public string Summarise()
    {
        if (Rules.Count == 0) return "built-in judgement";
        var first = Rules[0].Describe().ToLowerInvariant();
        return Rules.Count == 1 ? first : $"{first}, then {Rules.Count - 1} more";
    }
}

/// <summary>
/// One made-up copy in the wizard's worked example. The fields are the ones the rules can
/// compare, in the units somebody would type them in: minutes rather than seconds, megabytes
/// rather than bytes.
/// </summary>
public class SampleCopy : INotifyPropertyChanged
{
    private string _name = "";
    private double _minutes;
    private int _quality;
    private long _megabytes;
    private bool _sound = true;
    private bool _filed;

    public string Name { get => _name; set { _name = value; Changed(nameof(Name)); } }
    public double Minutes { get => _minutes; set { _minutes = value; Changed(nameof(Minutes)); } }
    public int Quality { get => _quality; set { _quality = value; Changed(nameof(Quality)); } }
    public long Megabytes { get => _megabytes; set { _megabytes = value; Changed(nameof(Megabytes)); } }
    public bool Sound { get => _sound; set { _sound = value; Changed(nameof(Sound)); } }
    public bool Filed { get => _filed; set { _filed = value; Changed(nameof(Filed)); } }

    /// <summary>The sample as the rules engine sees it — the same object a real file is.</summary>
    public MediaFile AsFile() => new()
    {
        FileName = Name,
        FullPath = @"D:\sample\" + Name,
        Extension = System.IO.Path.GetExtension(Name),
        DurationSeconds = Minutes * 60,
        Quality = Quality,
        SizeBytes = Megabytes * 1024 * 1024,
        Integrity = Sound ? IntegrityStatus.Ok : IntegrityStatus.Corrupt,
        Consolidated = Filed,
        LastModifiedUtc = new DateTime(2020, 1, 1).AddDays(Name.Length)
    };

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
/// the wizard. Two sample copies sit at the bottom with the figures anybody can change, and
/// every edit to the rules says, in the same words the real run will use, which of the two
/// would be kept and which step decided it.
/// </summary>
public class ConsolidationRulesWizard : Window
{
    private readonly ObservableCollection<ConsolidationRule> _rules = new();
    private readonly ObservableCollection<SampleCopy> _samples = new();

    private readonly ListBox _ruleList = new() { Height = 140 };
    private readonly ComboBox _field = new() { Width = 170 };
    private readonly ComboBox _prefer = new() { Width = 150 };
    private readonly TextBox _tolerance = new() { Width = 60, Text = "0" };
    private readonly ComboBox _match = new();
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

    public ConsolidationRulesWizard(string category, ConsolidationRuleSet existing)
    {
        Title = $"Consolidation rules for {category}";
        Width = 720; Height = 700;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _incomingMatch = existing.MatchBy;
        foreach (var rule in existing.Rules) _rules.Add(Copy(rule));
        _deepCheck.IsChecked = existing.DeepCheck;
        _fingerprint.IsChecked = existing.Fingerprint;

        SeedSamples(category);

        var root = new DockPanel { Margin = new Thickness(14) };
        var buttons = Buttons();
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        var body = new StackPanel();
        body.Children.Add(Intro(category));
        body.Children.Add(MatchingBox());
        body.Children.Add(StepsBox());
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
        Field = rule.Field, Prefer = rule.Prefer, Tolerance = rule.Tolerance
    };

    private static TextBlock Intro(string category) => new()
    {
        Text = $"When two files both claim to be the same {category.ToLowerInvariant()}, only one " +
               "of them belongs in the library. These steps decide which — in order, the first " +
               "one that can tell the copies apart having the final say. With no steps at all " +
               "the built-in judgement applies: the same content confirmed by fingerprint, then " +
               "the best picture, then the longest, then the smallest.",
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

    private GroupBox StepsBox()
    {
        // A rule writes itself out as its own sentence, so the list needs nothing but the
        // rules themselves.
        _ruleList.ItemsSource = _rules;

        var editor = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };

        _field.ItemsSource = Enum.GetValues(typeof(ConsolidationField));
        _field.SelectedItem = ConsolidationField.Quality;
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
        editor.Children.Add(_tolerance);
        editor.Children.Add(add);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        buttons.Children.Add(SmallButton("Move up", () => Move(-1)));
        buttons.Children.Add(SmallButton("Move down", () => Move(+1)));
        buttons.Children.Add(SmallButton("Remove", RemoveStep));
        buttons.Children.Add(SmallButton("Clear all", () => { _rules.Clear(); Recalculate(); }));

        var panel = new StackPanel();
        panel.Children.Add(_ruleList);
        panel.Children.Add(buttons);
        panel.Children.Add(editor);
        panel.Children.Add(_fieldHelp);
        ShowFieldHelp();

        return new GroupBox
        {
            Header = "The steps, in order",
            Margin = new Thickness(0, 0, 0, 10),
            Padding = new Thickness(8),
            Content = panel
        };
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

    private GroupBox SampleBox()
    {
        var grid = new DataGrid
        {
            ItemsSource = _samples,
            AutoGenerateColumns = false,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            Height = 110
        };

        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Name", Binding = new System.Windows.Data.Binding(nameof(SampleCopy.Name)), Width = 200
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Minutes", Binding = new System.Windows.Data.Binding(nameof(SampleCopy.Minutes)), Width = 70
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Quality", Binding = new System.Windows.Data.Binding(nameof(SampleCopy.Quality)), Width = 70
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "MB", Binding = new System.Windows.Data.Binding(nameof(SampleCopy.Megabytes)), Width = 70
        });
        grid.Columns.Add(new DataGridCheckBoxColumn
        {
            Header = "Sound", Binding = new System.Windows.Data.Binding(nameof(SampleCopy.Sound)), Width = 55
        });
        grid.Columns.Add(new DataGridCheckBoxColumn
        {
            Header = "Filed", Binding = new System.Windows.Data.Binding(nameof(SampleCopy.Filed)), Width = 55
        });

        // Recalculate the moment an edit is committed, which is what makes this a
        // demonstration rather than a table.
        grid.CellEditEnding += (_, _) => Dispatcher.BeginInvoke(new Action(Recalculate),
            System.Windows.Threading.DispatcherPriority.Background);
        foreach (var sample in _samples) sample.PropertyChanged += (_, _) => Recalculate();

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = "Two copies of one thing, made up. Change any figure and the answer below " +
                   "changes with it — it is worked out by the same code the real run uses.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = System.Windows.Media.Brushes.Gray,
            Margin = new Thickness(0, 0, 0, 6)
        });
        panel.Children.Add(grid);
        panel.Children.Add(_outcome);

        return new GroupBox
        {
            Header = "What would happen",
            Margin = new Thickness(0, 0, 0, 10),
            Padding = new Thickness(8),
            Content = panel
        };
    }

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
            Result = new ConsolidationRuleSet
            {
                MatchBy = _match.SelectedItem is DuplicateMatch m ? m : DuplicateMatch.SameContentOrTitle,
                Rules = _rules.Select(Copy).ToList(),
                DeepCheck = _deepCheck.IsChecked == true,
                Fingerprint = _fingerprint.IsChecked == true
            };
            DialogResult = true;
        };
        panel.Children.Add(ok);

        var cancel = new Button { Content = "Cancel", Width = 90, IsCancel = true };
        panel.Children.Add(cancel);
        return panel;
    }

    private Button SmallButton(string caption, Action onClick)
    {
        var button = new Button
        {
            Content = caption, Padding = new Thickness(10, 2, 10, 2),
            Margin = new Thickness(0, 0, 6, 0)
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    private void AddStep()
    {
        if (_field.SelectedItem is not ConsolidationField field) return;
        double.TryParse(_tolerance.Text, out var tolerance);

        _rules.Add(new ConsolidationRule
        {
            Field = field,
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
        _fieldHelp.Text = _field.SelectedItem is ConsolidationField field
            ? ConsolidationRule.Explain(field)
            : string.Empty;

    /// <summary>
    /// Sample copies worth arguing about: the same thing twice, one a better picture and the
    /// other longer, which is exactly the case the built-in rules hand to the user.
    /// </summary>
    private void SeedSamples(string category)
    {
        var audio = string.Equals(category, "Audio", StringComparison.OrdinalIgnoreCase);

        _samples.Add(new SampleCopy
        {
            Name = audio ? "Track (album version).flac" : "The Film (1080p).mkv",
            Minutes = audio ? 5.2 : 118,
            Quality = audio ? 320 : 1080,
            Megabytes = audio ? 42 : 4300,
            Sound = true,
            Filed = false
        });
        _samples.Add(new SampleCopy
        {
            Name = audio ? "Track (single edit).mp3" : "The Film (720p, extended).mkv",
            Minutes = audio ? 3.8 : 131,
            Quality = audio ? 192 : 720,
            Megabytes = audio ? 9 : 2100,
            Sound = true,
            Filed = true
        });
    }

    /// <summary>Run the rules over the samples and say, in words, what they decided.</summary>
    private void Recalculate()
    {
        var copies = _samples.Select(s => s.AsFile()).ToList();
        var verdict = ConsolidationRules.Choose(copies, _rules.ToList());

        if (_rules.Count == 0)
        {
            _outcome.Text = "With no steps, the built-in judgement decides: the copies are " +
                            "fingerprinted to confirm they are the same thing, and the best " +
                            "picture wins — with the longer copy preferred between two of equal " +
                            "quality, and the smaller between two otherwise alike.";
            _outcome.Foreground = System.Windows.Media.Brushes.DimGray;
            return;
        }

        if (verdict.Winner is not { } keeper)
        {
            _outcome.Text = $"Nothing would be filed: {verdict.Why}. These two copies would be " +
                            "put to you to choose between.";
            _outcome.Foreground = System.Windows.Media.Brushes.Firebrick;
            return;
        }

        var index = copies.IndexOf(keeper);
        var doomed = copies.Where(c => !ReferenceEquals(c, keeper)).Select(c => c.FileName);

        _outcome.Text = $"\"{_samples[index].Name}\" would be filed — {verdict.Why}. " +
                        $"{string.Join(", ", doomed)} would be deleted once it is.";
        _outcome.Foreground = System.Windows.Media.Brushes.DarkGreen;
    }
}
