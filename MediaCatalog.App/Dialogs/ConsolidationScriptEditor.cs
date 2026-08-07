using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MediaCatalog.Core.Consolidation;

namespace MediaCatalog.App;

/// <summary>One rule as the builder holds it: a condition made of pieces, and what to do.</summary>
public class ScriptLine : INotifyPropertyChanged
{
    private string _action = "Consolidate(File1)";

    public ObservableCollection<string> Condition { get; } = new();

    public string Action
    {
        get => _action;
        set { _action = value; Changed(nameof(Action)); Changed(nameof(Text)); }
    }

    /// <summary>The rule as a line of the language — which is the only thing that is saved.</summary>
    public string Text => Condition.Count == 0
        ? Action
        : $"if ({string.Join(" ", Condition)}) {Action}";

    public event PropertyChangedEventHandler? PropertyChanged;

    public void ConditionChanged() { Changed(nameof(Text)); }

    private void Changed(string property) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}

/// <summary>
/// Builds a comparison script by dragging its pieces into place.
///
/// The pieces are the whole of the language and nothing else: what can be dragged out of the
/// palette is what can be written, so a rule built here always reads, and a rule that reads
/// can always be picked up and rearranged here. The text underneath is not a second way of
/// saying it — it is the same thing, written out, and typing into it puts the pieces back the
/// way the text says.
///
/// Every rule is one line: a condition, and what to do when it holds. That shape is the
/// language's own — there are no blocks inside blocks to get lost in — and it is why a
/// dragged piece always has exactly one obvious place to land.
/// </summary>
public class ConsolidationScriptEditor : UserControl
{
    private const string PieceFormat = "MediaCatalog.ScriptPiece";

    private readonly ObservableCollection<ScriptLine> _lines = new();
    private readonly StackPanel _lineList = new();
    private readonly TextBox _number = new() { Width = 54, Text = "60" };
    private readonly TextBox _source = new()
    {
        AcceptsReturn = true, FontFamily = new FontFamily("Consolas, Courier New, monospace"),
        Height = 96, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        TextWrapping = TextWrapping.NoWrap
    };
    private readonly TextBlock _problem = new()
    {
        TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Firebrick,
        Margin = new Thickness(0, 4, 0, 0)
    };
    private readonly TextBlock _help = new()
    {
        TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray,
        Margin = new Thickness(0, 4, 0, 0), MinHeight = 44
    };

    private bool _writingSource;

    /// <summary>Raised whenever the script changes, so the worked example can be re-run.</summary>
    public event Action? Changed;

    public ConsolidationScriptEditor()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(238) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var palette = Palette();
        Grid.SetColumn(palette, 0);
        grid.Children.Add(palette);

        var right = Rules();
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);

        var root = new StackPanel();
        root.Children.Add(grid);
        root.Children.Add(SourceBox());
        root.Children.Add(_problem);

        Content = root;
    }

    /// <summary>The script as it stands, or an empty string when no rules have been built.</summary>
    public string Script
    {
        get => string.Join(Environment.NewLine, _lines.Select(l => l.Text));
        set => Load(value);
    }

    /// <summary>What is wrong with the script, or an empty string when it reads.</summary>
    public string Problem { get; private set; } = string.Empty;

    // --- The palette --------------------------------------------------------

    private FrameworkElement Palette()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };

        panel.Children.Add(new TextBlock
        {
            Text = "Drag a piece into a rule on the right. Click a piece already in a rule to " +
                   "take it out again.",
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray,
            Margin = new Thickness(0, 0, 0, 6)
        });

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Height = 300
        };
        var blocks = new StackPanel();

        blocks.Children.Add(Group("About the first file",
            RuleScriptVocabulary.Properties.Select(p => ($"File1.{p.Name}", p.Meaning))));
        blocks.Children.Add(Group("About the second file",
            RuleScriptVocabulary.Properties.Select(p => ($"File2.{p.Name}", p.Meaning))));
        blocks.Children.Add(Group("Compare them", new[]
        {
            (">", "The left is greater than the right."),
            (">=", "The left is greater than the right, or the same."),
            ("<", "The left is less than the right."),
            ("<=", "The left is less than the right, or the same."),
            ("==", "The two are the same."),
            ("!=", "The two are not the same.")
        }));
        blocks.Children.Add(Group("Join them up", new[]
        {
            ("AND", "Both halves have to hold."),
            ("OR", "Either half will do."),
            ("NOT", "The opposite of what follows."),
            ("(", "Open a bracket, for when the order the halves are read in matters."),
            (")", "Close a bracket."),
            ("true", "Always true."),
            ("false", "Always false.")
        }));
        blocks.Children.Add(Group("Find something out",
            RuleScriptVocabulary.Functions
                .Where(f => f.Name != "Consolidate")
                .SelectMany(Calls)));

        scroll.Content = blocks;
        panel.Children.Add(scroll);

        var numbers = new StackPanel
        {
            Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0)
        };
        numbers.Children.Add(new TextBlock
        {
            Text = "A number:", VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        });
        numbers.Children.Add(_number);
        numbers.Children.Add(Piece("the number", () => Number(),
            "Whatever is in the box beside this. Drag it in to compare against a figure of " +
            "your own — a size in bytes, a quality, a length in seconds."));
        panel.Children.Add(numbers);

        panel.Children.Add(_help);
        return panel;
    }

    /// <summary>The pieces one function offers: DeepScan needs a file, the rest do not.</summary>
    private static IEnumerable<(string, string)> Calls(RuleScriptVocabulary.Function function)
    {
        if (function.Name == "LengthDifferent")
        {
            yield return ($"LengthDifferent(60)", function.Meaning);
            yield break;
        }
        if (!function.TakesFile)
        {
            yield return ($"{function.Name}()", function.Meaning);
            yield break;
        }
        yield return ($"{function.Name}(File1)", function.Meaning);
        yield return ($"{function.Name}(File2)", function.Meaning);
    }

    private FrameworkElement Group(string header, IEnumerable<(string Text, string Meaning)> pieces)
    {
        var wrap = new WrapPanel { Margin = new Thickness(0, 2, 0, 8) };
        foreach (var (text, meaning) in pieces)
            wrap.Children.Add(Piece(text, () => Substitute(text), meaning));

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = header, FontWeight = FontWeights.Bold });
        panel.Children.Add(wrap);
        return panel;
    }

    /// <summary>The typed number put into the pieces that carry one.</summary>
    private string Substitute(string text) =>
        text.StartsWith("LengthDifferent(", StringComparison.Ordinal)
            ? $"LengthDifferent({Number()})"
            : text;

    private string Number() =>
        double.TryParse(_number.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value.ToString("0.##", CultureInfo.InvariantCulture)
            : "0";

    /// <summary>A draggable block. What it drops is worked out at the moment it is dragged,
    /// so the number box beside the palette is read then rather than when the page was built.</summary>
    private Border Piece(string caption, Func<string> payload, string meaning)
    {
        var block = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xE8, 0xEF, 0xF7)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x9E, 0xB4, 0xCE)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(0, 0, 4, 4),
            Cursor = Cursors.Hand,
            ToolTip = new TextBlock { Text = meaning, TextWrapping = TextWrapping.Wrap, MaxWidth = 380 },
            Child = new TextBlock { Text = caption }
        };

        block.MouseEnter += (_, _) => _help.Text = $"{caption} — {meaning}";

        // The drag starts once the pointer has actually moved, rather than the moment the
        // button goes down: a press that turns out to be a click should come to nothing
        // instead of holding the mouse hostage until it is let go.
        Point? from = null;
        block.PreviewMouseLeftButtonDown += (_, e) => from = e.GetPosition(this);
        block.PreviewMouseLeftButtonUp += (_, _) => from = null;
        block.PreviewMouseMove += (_, e) =>
        {
            if (from is not { } start || e.LeftButton != MouseButtonState.Pressed) return;

            var now = e.GetPosition(this);
            if (Math.Abs(now.X - start.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(now.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;

            from = null;
            DragDrop.DoDragDrop(block, new DataObject(PieceFormat, payload()), DragDropEffects.Copy);
        };
        return block;
    }

    // --- The rules ----------------------------------------------------------

    private FrameworkElement Rules()
    {
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Height = 300, Content = _lineList,
            BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(1),
            Padding = new Thickness(4),
            // Filled in rather than left blank: an empty background is not something a drop
            // can land on, and the empty space below the rules is where the first one starts.
            Background = Brushes.White
        };
        AcceptPieces(scroll);
        scroll.Drop += (_, e) =>
        {
            if (e.Data.GetData(PieceFormat) is not string piece) return;
            var line = new ScriptLine();
            line.Condition.Add(piece);
            _lines.Add(line);
            Rebuilt();
            e.Handled = true;
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        buttons.Children.Add(Button("Add a rule", () =>
        {
            _lines.Add(new ScriptLine());
            Rebuilt();
        }));
        buttons.Children.Add(Button("Start from an example", () =>
        {
            Load(RuleScriptVocabulary.Example);
            Rebuilt();
        }));
        buttons.Children.Add(Button("Clear", () => { _lines.Clear(); Rebuilt(); }));

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = "Each rule is read in turn, and the first one that both holds and names a " +
                   "copy ends the comparison. What it names is the copy that is kept.",
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray,
            Margin = new Thickness(0, 0, 0, 6)
        });
        panel.Children.Add(scroll);
        panel.Children.Add(buttons);
        return panel;
    }

    private Button Button(string caption, Action onClick)
    {
        var button = new Button
        {
            Content = caption, Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 0, 6, 0)
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>One rule's row: "if (" pieces ")" and the thing to do.</summary>
    private FrameworkElement Row(ScriptLine line)
    {
        var pieces = new WrapPanel { MinWidth = 220, MinHeight = 24, Background = Brushes.White };
        AcceptPieces(pieces);

        void Redraw()
        {
            pieces.Children.Clear();
            for (var i = 0; i < line.Condition.Count; i++)
            {
                var index = i;
                var chip = Chip(line.Condition[i]);
                chip.MouseLeftButtonUp += (_, _) =>
                {
                    line.Condition.RemoveAt(index);
                    Redraw();
                    Rebuilt();
                };
                AcceptPieces(chip);
                chip.Drop += (_, e) =>
                {
                    if (e.Data.GetData(PieceFormat) is not string dropped) return;
                    line.Condition.Insert(index, dropped);
                    Redraw();
                    Rebuilt();
                    e.Handled = true;
                };
                pieces.Children.Add(chip);
            }

            if (line.Condition.Count == 0)
                pieces.Children.Add(new TextBlock
                {
                    Text = "always",
                    Foreground = Brushes.Gray, FontStyle = FontStyles.Italic,
                    Margin = new Thickness(2, 2, 2, 2)
                });
        }

        pieces.Drop += (_, e) =>
        {
            if (e.Data.GetData(PieceFormat) is not string dropped) return;
            line.Condition.Add(dropped);
            Redraw();
            Rebuilt();
            e.Handled = true;
        };

        Redraw();

        var action = new ComboBox { Width = 168, Margin = new Thickness(6, 0, 6, 0) };
        action.ItemsSource = ActionChoices();
        action.SelectedItem = ActionChoices().Contains(line.Action) ? line.Action : ActionChoices()[0];
        line.Action = (string)action.SelectedItem;
        action.SelectionChanged += (_, _) =>
        {
            if (action.SelectedItem is string chosen) line.Action = chosen;
            Rebuilt();
        };

        var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2), LastChildFill = true };

        var remove = new Button
        {
            Content = "✕", Width = 22, Padding = new Thickness(0),
            ToolTip = "Take this rule out."
        };
        remove.Click += (_, _) => { _lines.Remove(line); Rebuilt(); };
        DockPanel.SetDock(remove, Dock.Right);
        row.Children.Add(remove);

        var up = new Button { Content = "▲", Width = 22, Padding = new Thickness(0), ToolTip = "Earlier." };
        up.Click += (_, _) => Move(line, -1);
        DockPanel.SetDock(up, Dock.Right);
        row.Children.Add(up);

        var down = new Button { Content = "▼", Width = 22, Padding = new Thickness(0), ToolTip = "Later." };
        down.Click += (_, _) => Move(line, +1);
        DockPanel.SetDock(down, Dock.Right);
        row.Children.Add(down);

        DockPanel.SetDock(action, Dock.Right);
        row.Children.Add(action);

        var opening = new TextBlock
        {
            Text = "if (", VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0), FontFamily = new FontFamily("Consolas")
        };
        DockPanel.SetDock(opening, Dock.Left);
        row.Children.Add(opening);

        var closing = new TextBlock
        {
            Text = ")", VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0), FontFamily = new FontFamily("Consolas")
        };
        DockPanel.SetDock(closing, Dock.Right);
        row.Children.Add(closing);

        row.Children.Add(new Border
        {
            BorderBrush = Brushes.Gainsboro, BorderThickness = new Thickness(1),
            Background = Brushes.White, MinHeight = 26, Padding = new Thickness(2),
            Child = pieces
        });

        return row;
    }

    private static string[] ActionChoices() => new[]
    {
        "Consolidate(File1)", "Consolidate(File2)", "Undecided",
        "DeepScan(File1)", "DeepScan(File2)", "FingerprintFiles()"
    };

    /// <summary>
    /// Take pieces, and say so while one is being dragged over: a drop target that gives no
    /// sign it is one leaves the user dragging a block around looking for somewhere it fits.
    /// </summary>
    private static void AcceptPieces(UIElement element)
    {
        element.AllowDrop = true;
        element.DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(PieceFormat) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
    }

    private Border Chip(string text) => new()
    {
        Background = new SolidColorBrush(Color.FromRgb(0xDF, 0xEE, 0xDF)),
        BorderBrush = new SolidColorBrush(Color.FromRgb(0x8F, 0xB8, 0x8F)),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(3),
        Padding = new Thickness(5, 1, 5, 1),
        Margin = new Thickness(0, 1, 3, 1),
        Cursor = Cursors.Hand,
        ToolTip = "Click to take this out. Drop another piece on it to put one in front.",
        Child = new TextBlock { Text = text, FontFamily = new FontFamily("Consolas") }
    };

    private void Move(ScriptLine line, int by)
    {
        var index = _lines.IndexOf(line);
        var to = index + by;
        if (index < 0 || to < 0 || to >= _lines.Count) return;
        _lines.Move(index, to);
        Rebuilt();
    }

    // --- The same thing, written out ---------------------------------------

    private FrameworkElement SourceBox()
    {
        _source.TextChanged += (_, _) =>
        {
            if (_writingSource) return;
            Load(_source.Text, fromText: true);
            Validate();
            Changed?.Invoke();
        };

        var expander = new Expander
        {
            Header = "Write it out instead",
            Margin = new Thickness(0, 8, 0, 0),
            Content = new StackPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = "The same rules as text. Anything typed here is read straight " +
                               "back into the pieces above, so the two can never drift apart. " +
                               "A line beginning with # is a note to yourself.",
                        TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray,
                        Margin = new Thickness(0, 4, 0, 4)
                    },
                    _source
                }
            }
        };
        return expander;
    }

    /// <summary>Read a script into the pieces, leaving what will not read alone.</summary>
    private void Load(string? script, bool fromText = false)
    {
        if (!RuleScriptParser.TryParse(script ?? string.Empty, out var program, out var error))
        {
            Problem = error;
            _problem.Text = error;
            return;
        }

        Problem = string.Empty;
        _problem.Text = string.Empty;

        _lines.Clear();
        foreach (var statement in program.Statements)
        {
            var line = new ScriptLine { Action = RuleScriptParser.Written(statement.Action) };
            foreach (var piece in RuleScriptParser.Pieces(statement.Condition))
                line.Condition.Add(piece);
            _lines.Add(line);
        }

        if (!fromText) WriteSource();
        Refresh();
    }

    /// <summary>
    /// Redraw the rows. Built in code rather than from a template because each one wires its
    /// own drag targets — the pieces inside it are where things land.
    /// </summary>
    private void Refresh()
    {
        _lineList.Children.Clear();
        foreach (var line in _lines) _lineList.Children.Add(Row(line));

        if (_lines.Count == 0)
            _lineList.Children.Add(new TextBlock
            {
                Text = "No rules of your own. Drag a piece in here, or press \"Start from an " +
                       "example\" and change what it says.",
                Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4)
            });
    }

    private void Rebuilt()
    {
        Refresh();
        WriteSource();
        Validate();
        Changed?.Invoke();
    }

    private void WriteSource()
    {
        _writingSource = true;
        try { _source.Text = Script; }
        finally { _writingSource = false; }
    }

    /// <summary>Say what is wrong with the script as it stands, if anything is.</summary>
    private void Validate()
    {
        var script = Script;
        if (string.IsNullOrWhiteSpace(script))
        {
            Problem = string.Empty;
            _problem.Text = string.Empty;
            return;
        }

        if (!RuleScriptParser.TryParse(script, out var program, out var error))
        {
            Problem = error;
            _problem.Text = error;
            return;
        }

        Problem = program.DecidesNothing
            ? "None of these rules keeps a copy, so nothing would ever be decided. " +
              "At least one has to end in Consolidate."
            : string.Empty;
        _problem.Text = Problem;
    }

}
