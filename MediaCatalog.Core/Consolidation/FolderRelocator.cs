using MediaCatalog.Core.Classification;
using MediaCatalog.Core.Models;
using MediaCatalog.Core.Storage;

namespace MediaCatalog.Core.Consolidation;

/// <param name="From">Where the folder was.</param>
/// <param name="To">Where it is now.</param>
/// <param name="Renamed">True when it stayed put and only its name changed.</param>
public record FolderMove(string From, string To, bool Renamed)
{
    public string Describe() => Renamed
        ? $"renamed '{Path.GetFileName(From)}' to '{Path.GetFileName(To)}'"
        : $"moved '{Path.GetFileName(From)}' into {Path.GetDirectoryName(To)}";
}

/// <summary>
/// Moves a whole folder that is already in the library but filed under the wrong name.
///
/// This is the difference between correcting a mistake and making a second copy. A show
/// folder spelled wrongly, or a film folder missing its year, holds files that are already
/// exactly where they should be relative to each other — every one of them wants to end up
/// in the same new folder. Copying each file out one at a time gets there, eventually, and
/// leaves the old folder standing empty behind it; renaming the folder gets there at once,
/// costs nothing whatever the folder holds, and leaves nothing behind.
///
/// Only ever applied when the whole folder agrees: if two files in it want two different
/// destinations, the folder is not simply misnamed and the files are relocated one by one
/// in the usual way.
/// </summary>
public static class FolderRelocator
{
    /// <summary>
    /// Try to fix <paramref name="files"/> by moving the folders they sit in. Returns what
    /// was moved; every entry whose file travelled has had its path updated.
    /// </summary>
    /// <param name="catalogue">
    /// Every catalogued file, so a folder is only moved when nothing else in it disagrees
    /// about where it should go.
    /// </param>
    public static List<FolderMove> Relocate(
        IReadOnlyList<MediaFile> files,
        IReadOnlyList<MediaFile> catalogue,
        AppSettings settings,
        Func<MediaFile, string> categoryOf)
    {
        var moves = new List<FolderMove>();

        // One attempt per folder, however many of its files were selected.
        var folders = files
            .Select(f => Path.GetDirectoryName(f.FullPath) ?? string.Empty)
            .Where(d => d.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var folder in folders)
        {
            if (PlanFor(folder, catalogue, settings, categoryOf) is not { } destination) continue;
            if (Move(folder, destination, catalogue) is { } move) moves.Add(move);
        }

        return moves;
    }

    /// <summary>
    /// Where a folder should be, or null when moving it is not the right answer: it is
    /// already there, it is not in the library, it holds files that want different homes,
    /// or something is already sitting at the destination.
    /// </summary>
    public static string? PlanFor(
        string folder,
        IReadOnlyList<MediaFile> catalogue,
        AppSettings settings,
        Func<MediaFile, string> categoryOf)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return null;

        var inFolder = catalogue
            .Where(f => string.Equals(Path.GetDirectoryName(f.FullPath), folder.TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (inFolder.Count == 0) return null;

        // Only folders already inside the library. Bringing a file in from elsewhere is a
        // move of that file, not of the folder it happened to be downloaded into.
        if (!inFolder.All(f => ConsolidationPlanner.IsUnderConsolidationRoot(f, settings)))
            return null;

        string? destination = null;
        foreach (var file in inFolder)
        {
            var planned = ConsolidationPlanner.PlanDirectory(file, categoryOf(file), settings);
            if (planned == null) return null;                       // nothing to say about it
            planned = planned.TrimEnd('\\', '/');

            if (destination == null) destination = planned;
            else if (!string.Equals(destination, planned, StringComparison.OrdinalIgnoreCase))
                return null;                                        // the folder is not of one mind
        }

        if (destination == null) return null;
        if (ConsolidationPlanner.PathsEqual(destination, folder)) return null;   // already right

        // Anything at all at the destination — a folder from an earlier attempt, a file of
        // that name — means this is a merge rather than a rename, and merges belong to the
        // per-file path where each collision can be put to the user.
        if (Directory.Exists(destination) || File.Exists(destination)) return null;

        // The files under it must be a folder's worth of files, not a folder that also
        // holds a hundred other things: subfolders are carried along by the move, and only
        // a folder whose contents are all accounted for can safely be taken as a unit.
        return HasUncataloguedSubfolders(folder) ? null : destination;
    }

    /// <summary>
    /// Carry out the move and update every catalogue entry beneath the folder. Null when
    /// the filesystem refused — a different drive, a permissions problem, a file held open
    /// — in which case the caller falls back on relocating the files one by one.
    /// </summary>
    private static FolderMove? Move(string folder, string destination, IReadOnlyList<MediaFile> catalogue)
    {
        var source = folder.TrimEnd('\\', '/');
        var parent = Path.GetDirectoryName(destination);

        try
        {
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            Directory.Move(source, destination);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        // Everything that was under the old folder is now under the new one, at the same
        // place within it — a rename changes the prefix and nothing else.
        var prefix = source + Path.DirectorySeparatorChar;
        foreach (var file in catalogue)
        {
            if (!file.FullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            file.FullPath = Path.Combine(destination, file.FullPath[prefix.Length..]);
            file.FileName = Path.GetFileName(file.FullPath);
        }

        var sameParent = string.Equals(Path.GetDirectoryName(source), parent,
            StringComparison.OrdinalIgnoreCase);
        return new FolderMove(source, destination, sameParent);
    }

    /// <summary>
    /// True when the folder holds subfolders. Those travel with a move, which is right for
    /// a show's season folders but wrong if the folder turns out to be somebody's whole
    /// library; the conservative reading is to leave it to the per-file path.
    /// </summary>
    private static bool HasUncataloguedSubfolders(string folder)
    {
        try { return Directory.EnumerateDirectories(folder).Any(); }
        catch { return true; }
    }
}
