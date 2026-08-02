using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using MediaCatalog.App.ViewModels;
using MediaCatalog.Core.Models;

namespace MediaCatalog.App;

/// <summary>
/// The one delete conversation in the program: confirm what is about to go — every file
/// listed, however the batch was arrived at — delete it through the shared deleter, which
/// clears read-only files rather than giving up on them, offer another go with
/// administrative rights when permissions were the only thing in the way, and clear away
/// any folder the delete has left standing empty.
///
/// Everywhere that deletes goes through here, so the results grid, the duplicate managers
/// and the unhashed-files list all behave the same way and gain the same fixes — including
/// the option to skip the Recycle Bin, which is now offered wherever files are removed.
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

        var dialog = new DeleteFilesWindow(files, vm.Settings.SkipRecycleBinByDefault) { Owner = owner };
        if (dialog.ShowDialog() != true) return false;

        var recycle = !dialog.DeletePermanently;
        var wanted = files.Select(f => f.FullPath).ToList();
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
                await OfferEmptyFoldersAsync(owner, vm, wanted, recycle);
                return true;
            }
        }

        MessageBox.Show(owner, outcome.Message, title,
            MessageBoxButton.OK,
            outcome.Result.Failed > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);

        if (outcome.Result.Deleted > 0)
            await OfferEmptyFoldersAsync(owner, vm, wanted, recycle);

        return outcome.Result.Deleted > 0;
    }

    /// <summary>
    /// A folder whose last file has just been deleted is left holding nothing. Offer to
    /// take it away too, naming the folders so the answer is an informed one — and take
    /// their parents with them when those are emptied in turn, which is what happens to a
    /// season folder that was the last season of a show.
    /// </summary>
    private static async Task OfferEmptyFoldersAsync(
        Window owner, MainViewModel vm, IReadOnlyList<string> deletedPaths, bool toRecycleBin)
    {
        if (!vm.Settings.OfferRemoveEmptyFolders) return;

        var empty = vm.EmptyFoldersLeftBy(deletedPaths);
        if (empty.Count == 0) return;

        var listed = string.Join("\n", empty.Take(15).Select(f => "    " + f));
        if (empty.Count > 15) listed += $"\n    …and {empty.Count - 15} more";

        var ask = MessageBox.Show(owner,
            (empty.Count == 1
                ? "That was the last file in its folder, which now holds nothing:\n\n"
                : $"{empty.Count} folders now hold nothing:\n\n") +
            listed +
            "\n\nRemove them as well? Any parent folder they leave empty goes too.",
            "Empty folders", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (ask != MessageBoxResult.Yes) return;

        var removed = await vm.RemoveEmptyFoldersAsync(empty, toRecycleBin);
        MessageBox.Show(owner,
            removed == 0
                ? "The folders could not be removed — something else may be using them."
                : $"{removed} empty folder(s) removed.",
            "Empty folders", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
