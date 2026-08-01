using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using MediaCatalog.App.ViewModels;
using MediaCatalog.Core.Models;

namespace MediaCatalog.App;

/// <summary>
/// The one delete conversation in the program: confirm what is about to go, delete it
/// through the shared deleter — which clears read-only files rather than giving up on
/// them — and, when the only thing in the way was permissions, offer to try again with
/// administrative rights.
///
/// Everywhere that deletes goes through here, so the results grid, the duplicate manager
/// and the unhashed-files list all behave the same way and gain the same fixes.
/// </summary>
public static class FileDeletion
{
    /// <summary>
    /// Run the whole conversation. Returns true when files were actually deleted, so the
    /// caller can refresh whatever it is showing.
    /// </summary>
    public static async Task<bool> RunAsync(
        Window owner, MainViewModel vm, IReadOnlyList<MediaFile> files, string title = "Delete files")
    {
        if (files.Count == 0)
        {
            MessageBox.Show(owner, "Select the files to delete first.",
                title, MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        var dialog = new DeleteFilesWindow(files) { Owner = owner };
        if (dialog.ShowDialog() != true) return false;

        var recycle = !dialog.DeletePermanently;
        var outcome = await vm.DeleteFilesAsync(files, recycle);

        // Files that refused over permissions are worth one more go with more rights; ones
        // held open by another application need that application closed instead.
        var denied = outcome.AccessDeniedPaths;
        if (denied.Count > 0)
        {
            var retry = MessageBox.Show(owner,
                outcome.Message +
                $"\n\n{denied.Count} file(s) were refused for permission reasons. " +
                "Retry those with administrative rights? Windows will ask you to confirm.",
                title, MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (retry == MessageBoxResult.Yes)
            {
                var stillThere = files.Where(f => denied.Contains(f.FullPath)).ToList();
                var elevated = await vm.RetryDeleteElevatedAsync(stillThere, recycle);
                MessageBox.Show(owner, elevated.Message, title,
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return true;
            }
        }

        MessageBox.Show(owner, outcome.Message, title,
            MessageBoxButton.OK,
            outcome.Result.Failed > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        return outcome.Result.Deleted > 0;
    }
}
