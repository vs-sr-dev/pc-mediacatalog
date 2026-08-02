using System.Diagnostics;
using System.IO;
using System.Windows;

namespace MediaCatalog.App;

/// <summary>
/// Hands a path to Windows: open it with whatever application is associated with it, or
/// show it in Explorer. Shared, because several dialogs now offer both and a failure is
/// worth the same sentence wherever it happens.
/// </summary>
public static class ShellOpen
{
    /// <summary>Open a file with its associated application.</summary>
    public static void Open(Window owner, string path)
    {
        if (!File.Exists(path))
        {
            MessageBox.Show(owner, "The file no longer exists on disk.",
                "Open file", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (System.Exception ex)
        {
            MessageBox.Show(owner, $"Could not open the file:\n{ex.Message}",
                "Open file", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Show a file in Explorer with it selected — or, when the file has gone, open the
    /// folder it was in, which is usually what the user was after anyway.
    /// </summary>
    public static void SelectInExplorer(Window owner, string path)
    {
        try
        {
            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
                { UseShellExecute = true });
                return;
            }

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
            else
                MessageBox.Show(owner, "The containing folder no longer exists.",
                    "Open folder", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (System.Exception ex)
        {
            MessageBox.Show(owner, $"Could not open the folder:\n{ex.Message}",
                "Open folder", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
