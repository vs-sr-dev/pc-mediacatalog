using System.Collections.ObjectModel;
using System.Windows;
using MediaCatalog.App.ViewModels;

namespace MediaCatalog.App;

/// <summary>
/// Modal preview of proposed renames. On Apply it exposes the ticked proposals via
/// <see cref="SelectedRows"/> and returns a positive dialog result.
/// </summary>
public partial class RenamePreviewWindow : Window
{
    private readonly ObservableCollection<RenameRow> _rows;

    public RenamePreviewWindow(IEnumerable<RenameRow> rows)
    {
        InitializeComponent();
        _rows = new ObservableCollection<RenameRow>(rows);
        ProposalGrid.ItemsSource = _rows;
        UpdateCount();
    }

    public IReadOnlyList<RenameRow> SelectedRows =>
        _rows.Where(r => r.IsSelected).ToList();

    private void UpdateCount() =>
        CountText.Text = $"{_rows.Count(r => r.IsSelected)} of {_rows.Count} selected";

    private void OnSelectAll(object sender, RoutedEventArgs e)
    {
        foreach (var r in _rows) r.IsSelected = true;
        UpdateCount();
    }

    private void OnSelectNone(object sender, RoutedEventArgs e)
    {
        foreach (var r in _rows) r.IsSelected = false;
        UpdateCount();
    }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        if (SelectedRows.Count == 0)
        {
            MessageBox.Show(this, "Nothing ticked to rename.", "Rename",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
