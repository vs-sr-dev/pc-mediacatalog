using System.Windows;
using MediaCatalog.Core.Tools;
using Microsoft.Win32;

namespace MediaCatalog.App;

/// <summary>Lets the user point at ffmpeg/ffprobe/fpcalc and shows what was detected.</summary>
public partial class ToolSettingsWindow : Window
{
    public ToolSettings Result { get; private set; }

    public ToolSettingsWindow(ToolSettings current)
    {
        InitializeComponent();
        Result = current;
        FfmpegBox.Text = current.FfmpegPath;
        FfprobeBox.Text = current.FfprobePath;
        FpcalcBox.Text = current.FpcalcPath;
        ShowDetection();
    }

    private ToolSettings Collect() => new()
    {
        FfmpegPath = FfmpegBox.Text.Trim(),
        FfprobePath = FfprobeBox.Text.Trim(),
        FpcalcPath = FpcalcBox.Text.Trim()
    };

    private void ShowDetection()
    {
        var tools = ExternalTools.Resolve(Collect());
        string Line(string name, string? path) =>
            $"{name}: {(string.IsNullOrEmpty(path) ? "not found" : path)}";
        StatusBox.Text =
            Line("ffmpeg", tools.FfmpegPath) + "   |   " +
            Line("ffprobe", tools.FfprobePath) + "   |   " +
            Line("fpcalc", tools.FpcalcPath);
    }

    private static string? Browse()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Select executable",
            Filter = "Executables (*.exe)|*.exe|All files (*.*)|*.*"
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    private void BrowseFfmpeg(object sender, RoutedEventArgs e)
    { if (Browse() is { } p) { FfmpegBox.Text = p; ShowDetection(); } }

    private void BrowseFfprobe(object sender, RoutedEventArgs e)
    { if (Browse() is { } p) { FfprobeBox.Text = p; ShowDetection(); } }

    private void BrowseFpcalc(object sender, RoutedEventArgs e)
    { if (Browse() is { } p) { FpcalcBox.Text = p; ShowDetection(); } }

    private void OnRedetect(object sender, RoutedEventArgs e) => ShowDetection();

    private void OnSave(object sender, RoutedEventArgs e)
    {
        Result = Collect();
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
