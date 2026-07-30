using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace MediaCatalog.App;

/// <summary>A one-line text prompt (used for "add new category").</summary>
public class PromptWindow : Window
{
    private readonly TextBox _box = new();

    private PromptWindow(string title, string prompt)
    {
        Title = title; Width = 400; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ResizeMode = ResizeMode.NoResize;

        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock
        {
            Text = prompt, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6)
        });
        panel.Children.Add(_box);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        var ok = new Button { Content = "OK", Width = 74, IsDefault = true, Margin = new Thickness(0, 0, 6, 0) };
        ok.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(ok);
        buttons.Children.Add(new Button { Content = "Cancel", Width = 74, IsCancel = true });
        panel.Children.Add(buttons);
        Content = panel;
    }

    public static string? Ask(Window owner, string title, string prompt, string initial = "")
    {
        var w = new PromptWindow(title, prompt) { Owner = owner };
        w._box.Text = initial;
        w._box.SelectAll();
        return w.ShowDialog() == true ? w._box.Text : null;
    }
}

/// <summary>A read-only scrollable list (used for the missing-files report).</summary>
public class ListWindow : Window
{
    public ListWindow(string title, string message, IEnumerable<string> items)
    {
        Title = title; Width = 720; Height = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var dock = new DockPanel { Margin = new Thickness(12) };
        var msg = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(msg, Dock.Top);
        dock.Children.Add(msg);
        var close = new Button
        {
            Content = "Close", Width = 84, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0), IsCancel = true
        };
        DockPanel.SetDock(close, Dock.Bottom);
        dock.Children.Add(close);
        dock.Children.Add(new ListBox
        {
            ItemsSource = items.ToList(),
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        });
        Content = dock;
    }
}

/// <summary>Choose a category, the target folder (this one or a parent), and subfolder scope.</summary>
public class CategoryFolderWindow : Window
{
    private readonly ComboBox _combo = new() { IsEditable = true };
    private readonly ComboBox _folderCombo = new();
    private readonly CheckBox _subdirs = new() { Content = "Include all subfolders", IsChecked = true };

    public string SelectedCategory => _combo.Text.Trim();
    public string SelectedFolder => _folderCombo.SelectedItem as string ?? _folderCombo.Text;
    public bool IncludeSubdirectories => _subdirs.IsChecked == true;

    public CategoryFolderWindow(string folder, IReadOnlyList<string> categories)
    {
        Title = "Set category for folder"; Width = 520; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ResizeMode = ResizeMode.NoResize;

        var panel = new StackPanel { Margin = new Thickness(14) };

        // The folder and each of its ancestors, so a rule can be applied higher up the tree.
        var ancestors = new List<string>();
        var d = folder;
        while (!string.IsNullOrEmpty(d))
        {
            ancestors.Add(d);
            d = System.IO.Path.GetDirectoryName(d);
        }
        panel.Children.Add(new TextBlock { Text = "Apply to folder (pick this one or a parent):" });
        _folderCombo.ItemsSource = ancestors;
        _folderCombo.SelectedIndex = 0;
        _folderCombo.Margin = new Thickness(0, 2, 0, 10);
        panel.Children.Add(_folderCombo);

        panel.Children.Add(new TextBlock { Text = "Category (pick or type a new one):" });
        _combo.ItemsSource = categories;
        if (categories.Count > 0) _combo.SelectedIndex = 0;
        _combo.Margin = new Thickness(0, 2, 0, 10);
        panel.Children.Add(_combo);
        panel.Children.Add(_subdirs);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0)
        };
        var ok = new Button { Content = "OK", Width = 74, IsDefault = true, Margin = new Thickness(0, 0, 6, 0) };
        ok.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(SelectedCategory)) DialogResult = true; };
        buttons.Children.Add(ok);
        buttons.Children.Add(new Button { Content = "Cancel", Width = 74, IsCancel = true });
        panel.Children.Add(buttons);
        Content = panel;
    }
}
