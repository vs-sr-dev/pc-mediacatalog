using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MediaCatalog.App.Infrastructure;

namespace MediaCatalog.App;

/// <summary>
/// The About box: the program icon, what version this build is, and who made it.
/// </summary>
public class AboutWindow : Window
{
    public AboutWindow()
    {
        Title = "About Media Catalog";
        Width = 430;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var layout = new DockPanel { Margin = new Thickness(18) };

        if (LoadIcon() is { } icon)
        {
            var image = new Image
            {
                Source = icon,
                Width = 64, Height = 64,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 0, 16, 0)
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            DockPanel.SetDock(image, Dock.Left);
            layout.Children.Add(image);
        }

        var ok = new Button
        {
            Content = "OK", Width = 84, IsDefault = true, IsCancel = true,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };
        ok.Click += (_, _) => Close();
        DockPanel.SetDock(ok, Dock.Bottom);
        layout.Children.Add(ok);

        var text = new StackPanel();
        text.Children.Add(new TextBlock
        {
            Text = "Media Catalog",
            FontSize = 20, FontWeight = FontWeights.Bold
        });
        text.Children.Add(new TextBlock
        {
            Text = $"Version {AppVersion.Product}",
            Margin = new Thickness(0, 4, 0, 0)
        });
        text.Children.Add(new TextBlock
        {
            Text = $"File version {AppVersion.File}",
            Foreground = Brushes.Gray,
            Margin = new Thickness(0, 1, 0, 0)
        });
        text.Children.Add(new TextBlock
        {
            Text = AppVersion.Credits,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 14, 0, 0)
        });
        layout.Children.Add(text);

        Content = layout;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    /// <summary>The application icon, or null if it can't be read — the box still opens.</summary>
    private static BitmapFrame? LoadIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/MediaCatalog.ico");
            using var stream = Application.GetResourceStream(uri)?.Stream;
            if (stream == null) return null;

            var decoder = new IconBitmapDecoder(stream,
                BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            // The largest frame in the .ico scales down to 64px cleanly; the smallest
            // would be a blur at that size.
            return decoder.Frames.OrderByDescending(f => f.PixelWidth).FirstOrDefault();
        }
        catch { return null; }
    }
}
