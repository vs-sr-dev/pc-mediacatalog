using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using MediaCatalog.App.Infrastructure;
using MediaCatalog.Core.Consolidation;

namespace MediaCatalog.App;

/// <summary>Toggle visibility of the results grid columns.</summary>
public class ColumnChooserWindow : Window
{
    public ColumnChooserWindow(DataGrid grid)
    {
        Title = "Columns"; Width = 240; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ResizeMode = ResizeMode.NoResize;

        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(new TextBlock { Text = "Show columns:", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 6) });

        foreach (var column in grid.Columns)
        {
            var col = column;
            var cb = new CheckBox
            {
                Content = col.Header?.ToString() ?? "(column)",
                IsChecked = col.Visibility == Visibility.Visible,
                Margin = new Thickness(0, 2, 0, 2)
            };
            cb.Checked += (_, _) => col.Visibility = Visibility.Visible;
            cb.Unchecked += (_, _) => col.Visibility = Visibility.Collapsed;
            panel.Children.Add(cb);
        }

        panel.Children.Add(new Button
        {
            Content = "Close", Width = 80, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0), IsCancel = true
        });
        Content = panel;
    }
}

/// <summary>
/// Shows proposed consolidation moves (current → new location, notes, collisions) and
/// lets the user tick which to apply. Recommended ones start ticked.
/// </summary>
public class ConsolidationSuggesterWindow : Window
{
    private sealed class Row : ObservableObject
    {
        public required ConsolidationSuggestion S { get; init; }
        private bool _selected;
        public bool IsSelected { get => _selected; set => SetProperty(ref _selected, value); }
        public string Category => S.Category;
        public string Current => S.CurrentPath;
        public string Proposed => S.ProposedPath;
        public string Note => S.NameCollision && !string.IsNullOrEmpty(S.Note) ? "⚠ " + S.Note : S.Note;
    }

    private readonly ObservableCollection<Row> _rows;

    public IReadOnlyList<ConsolidationSuggestion> Selected =>
        _rows.Where(r => r.IsSelected).Select(r => r.S).ToList();

    public ConsolidationSuggesterWindow(IReadOnlyList<ConsolidationSuggestion> suggestions)
    {
        Title = "Suggested consolidation"; Width = 1000; Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _rows = new ObservableCollection<Row>(
            suggestions.Select(s => new Row { S = s, IsSelected = s.Recommended }));

        var dock = new DockPanel { Margin = new Thickness(12) };

        var hint = new TextBlock
        {
            Text = "Review the proposed moves. Recommended items are ticked. Rows with a ⚠ have a " +
                   "name collision at the destination; lower-quality duplicates and unvalidated TV " +
                   "titles are left unticked.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8)
        };
        DockPanel.SetDock(hint, Dock.Top);
        dock.Children.Add(hint);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        buttons.Children.Add(Btn("Select recommended", () => SetAll(r => r.S.Recommended)));
        buttons.Children.Add(Btn("Select all", () => SetAll(_ => true)));
        buttons.Children.Add(Btn("Select none", () => SetAll(_ => false)));
        var apply = new Button { Content = "Apply", Width = 100, IsDefault = true, FontWeight = FontWeights.Bold, Margin = new Thickness(12, 0, 6, 0) };
        apply.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(apply);
        buttons.Children.Add(new Button { Content = "Cancel", Width = 84, IsCancel = true });
        dock.Children.Add(buttons);

        var grid = new DataGrid
        {
            AutoGenerateColumns = false, CanUserAddRows = false, HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, ItemsSource = _rows,
            SelectionUnit = DataGridSelectionUnit.FullRow
        };
        grid.Columns.Add(new DataGridCheckBoxColumn
        {
            Header = "✓",
            Binding = new Binding(nameof(Row.IsSelected)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
            Width = 30
        });
        grid.Columns.Add(ReadOnlyText("Cat", nameof(Row.Category), 70));
        grid.Columns.Add(ReadOnlyStar("Current location", nameof(Row.Current)));
        grid.Columns.Add(ReadOnlyStar("New location", nameof(Row.Proposed)));
        grid.Columns.Add(ReadOnlyText("Notes", nameof(Row.Note), 220));
        dock.Children.Add(grid);

        Content = dock;
    }

    private void SetAll(System.Func<Row, bool> value)
    {
        foreach (var r in _rows) r.IsSelected = value(r);
    }

    private Button Btn(string text, System.Action onClick)
    {
        var b = new Button { Content = text, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 6, 0) };
        b.Click += (_, _) => onClick();
        return b;
    }

    private static DataGridTextColumn ReadOnlyText(string header, string path, double width) => new()
    {
        Header = header, Binding = new Binding(path), Width = width, IsReadOnly = true
    };

    private static DataGridTextColumn ReadOnlyStar(string header, string path) => new()
    {
        Header = header, Binding = new Binding(path),
        Width = new DataGridLength(1, DataGridLengthUnitType.Star), IsReadOnly = true
    };
}
