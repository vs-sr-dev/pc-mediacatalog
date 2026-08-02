using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using MediaCatalog.App.Infrastructure;
using MediaCatalog.Core.Storage;

namespace MediaCatalog.App;

/// <summary>
/// Lists the exclusion rules another rule already covers, and lets the user pick which of
/// them to drop.
///
/// "All or nothing" was never the right question. A broad rule can supersede a dozen
/// narrow ones, and it is perfectly reasonable to want ten of them gone and two kept —
/// perhaps because those two are about to be broadened again, or are a note to self about
/// a folder worth remembering. So each is offered on its own, with Select all and Select
/// none for when the answer really is all or nothing after all.
/// </summary>
public class RedundantRulesWindow : Window
{
    private sealed class Row : ObservableObject
    {
        public required ExcludedFolder Model { get; init; }
        private bool _selected = true;
        public bool IsSelected { get => _selected; set => SetProperty(ref _selected, value); }

        public string Path => Model.Path;
        public string Scope =>
            (AppSettings.HasWildcard(Model.Path) ? "pattern, " : "") +
            (Model.IncludeSubdirectories ? "+ subfolders" : "this folder only");
    }

    private readonly ObservableCollection<Row> _rows;

    /// <summary>The rules the user chose to remove. Empty when they kept the lot.</summary>
    public IReadOnlyList<ExcludedFolder> Chosen =>
        _rows.Where(r => r.IsSelected).Select(r => r.Model).ToList();

    /// <param name="covering">
    /// The rule that made these redundant, when one particular rule did. Null for a sweep
    /// of the whole list, where each is covered by a different rule.
    /// </param>
    public RedundantRulesWindow(IReadOnlyList<ExcludedFolder> superseded, ExcludedFolder? covering = null)
    {
        Title = "Redundant rules"; Width = 720; Height = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _rows = new ObservableCollection<Row>(superseded.Select(r => new Row { Model = r }));

        var dock = new DockPanel { Margin = new Thickness(14) };

        var heading = new TextBlock
        {
            Text = covering != null
                ? $"'{covering.Path}' already covers {superseded.Count} existing rule(s)."
                : $"{superseded.Count} rule(s) are already covered by a broader rule.",
            FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 4)
        };
        DockPanel.SetDock(heading, Dock.Top);
        dock.Children.Add(heading);

        var explain = new TextBlock
        {
            Text = "Tick the ones to remove. Nothing about what gets excluded changes either way — " +
                   "the list is simply shorter. Untick any you would rather keep; leaving them " +
                   "costs nothing but clutter.",
            Foreground = System.Windows.Media.Brushes.Gray,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8)
        };
        DockPanel.SetDock(explain, Dock.Top);
        dock.Children.Add(explain);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0)
        };
        buttons.Children.Add(Btn("Select all", () => SetAll(true)));
        buttons.Children.Add(Btn("Select none", () => SetAll(false)));

        var ok = new Button
        {
            Content = "Remove ticked", Width = 130, IsDefault = true,
            FontWeight = FontWeights.Bold, Margin = new Thickness(12, 0, 6, 0)
        };
        ok.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(ok);
        buttons.Children.Add(new Button { Content = "Keep them all", Width = 110, IsCancel = true });
        DockPanel.SetDock(buttons, Dock.Bottom);
        dock.Children.Add(buttons);

        var grid = new DataGrid
        {
            AutoGenerateColumns = false, CanUserAddRows = false,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            ItemsSource = _rows, SelectionUnit = DataGridSelectionUnit.FullRow
        };
        grid.Columns.Add(new DataGridCheckBoxColumn
        {
            Header = "✓",
            Binding = new Binding(nameof(Row.IsSelected))
            { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
            Width = 34
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Rule", Binding = new Binding(nameof(Row.Path)), IsReadOnly = true,
            Width = new DataGridLength(1, DataGridLengthUnitType.Star)
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Scope", Binding = new Binding(nameof(Row.Scope)), IsReadOnly = true, Width = 150
        });
        dock.Children.Add(grid);

        Content = dock;
    }

    private void SetAll(bool value)
    {
        foreach (var r in _rows) r.IsSelected = value;
    }

    private Button Btn(string text, Action onClick)
    {
        var b = new Button { Content = text, Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 0, 6, 0) };
        b.Click += (_, _) => onClick();
        return b;
    }

    /// <summary>
    /// Ask, and hand back what to remove — the shape <see cref="AppSettings.PruneSuperseded"/>
    /// wants. An empty list means "keep them all", which is also what closing the window says.
    /// </summary>
    public static IReadOnlyList<ExcludedFolder> Ask(
        Window owner, IReadOnlyList<ExcludedFolder> superseded, ExcludedFolder? covering = null)
    {
        var dlg = new RedundantRulesWindow(superseded, covering) { Owner = owner };
        return dlg.ShowDialog() == true ? dlg.Chosen : Array.Empty<ExcludedFolder>();
    }
}
