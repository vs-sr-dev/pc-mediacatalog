using System.IO;
using System.Windows;
using MediaCatalog.Core.Relocation;

namespace MediaCatalog.App;

/// <summary>
/// Application entry point. Normally it shows the main window — or leaves it hidden in the
/// notification area when Windows launched us at sign-in — but it also serves as its own
/// elevated helper: relaunched with <c>--delete-elevated</c> it deletes the listed files
/// with administrative rights and exits, which is how a permission-denied delete is retried.
/// </summary>
public partial class App : Application
{
    /// <summary>Argument the Run-key registration adds so a sign-in launch starts hidden.</summary>
    public const string TrayArgument = "--tray";

    /// <summary>Argument for the elevated delete helper, followed by a file of paths.</summary>
    public const string DeleteArgument = "--delete-elevated";

    /// <summary>Modifier for the helper: bypass the Recycle Bin.</summary>
    public const string PermanentArgument = "--permanent";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var args = e.Args;
        var deleteIndex = Array.FindIndex(args, a =>
            string.Equals(a, DeleteArgument, StringComparison.OrdinalIgnoreCase));

        if (deleteIndex >= 0 && deleteIndex + 1 < args.Length)
        {
            RunElevatedDelete(args[deleteIndex + 1],
                permanent: args.Contains(PermanentArgument, StringComparer.OrdinalIgnoreCase));
            Shutdown();
            return;
        }

        var startHidden = args.Contains(TrayArgument, StringComparer.OrdinalIgnoreCase);
        var window = new MainWindow(startHidden);
        MainWindow = window;
        if (!startHidden) window.Show();
    }

    /// <summary>
    /// Delete the paths listed in <paramref name="listPath"/> (one per line) and write the
    /// outcome beside it as <c>&lt;list&gt;.result</c>: one line per failure, empty if all
    /// went. The parent process reads that back rather than guessing.
    /// </summary>
    private static void RunElevatedDelete(string listPath, bool permanent)
    {
        try
        {
            var paths = File.ReadAllLines(listPath)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .ToList();
            var result = FileDeleter.Delete(paths, toRecycleBin: !permanent);
            File.WriteAllLines(listPath + ".result",
                result.Failures.Select(f => f.Path + "\t" + f.Reason));
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(listPath + ".result", "\t" + ex.Message); } catch { }
        }
    }
}
