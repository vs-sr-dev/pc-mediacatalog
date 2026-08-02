namespace MediaCatalog.Core.Relocation;

/// <summary>
/// Tidies up the folders an operation has just emptied.
///
/// Deleting the last file in a folder leaves a folder that holds nothing — litter from an
/// operation the user asked for, and the sort of thing they would have to go and find
/// themselves afterwards. This works out which folders those are so the caller can offer
/// to remove them; it never removes anything without being told to.
/// </summary>
public static class EmptyFolderCleaner
{
    /// <summary>
    /// The folders that held <paramref name="deletedPaths"/> and now hold nothing at all —
    /// no files, no subfolders — deepest first, so removing one can empty its parent and
    /// that parent is offered on the next pass.
    ///
    /// A drive root is never offered: it is empty far more often than it is unwanted.
    /// </summary>
    public static List<string> EmptiedBy(IEnumerable<string> deletedPaths) =>
        EmptyAmong(deletedPaths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => Path.GetDirectoryName(p) ?? string.Empty));

    /// <summary>
    /// The same question asked of folders directly, for callers that moved files out of
    /// them rather than deleting files inside them.
    /// </summary>
    public static List<string> EmptyAmong(IEnumerable<string> folders) =>
        folders
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(IsEmpty)
            .OrderByDescending(d => d.Length)
            .ToList();

    /// <summary>True when the folder exists, is not a drive root, and holds nothing.</summary>
    public static bool IsEmpty(string folder)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return false;

            var root = Path.GetPathRoot(folder);
            if (string.Equals(folder.TrimEnd('\\', '/'), root?.TrimEnd('\\', '/'),
                    StringComparison.OrdinalIgnoreCase))
                return false;

            return !Directory.EnumerateFileSystemEntries(folder).Any();
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// Remove the given folders, and then any parent they have just emptied in turn — a
    /// season folder going often leaves the show folder with nothing in it either.
    /// Returns what actually went.
    /// </summary>
    public static List<string> Remove(IEnumerable<string> folders, bool toRecycleBin)
    {
        var removed = new List<string>();

        foreach (var folder in folders.OrderByDescending(f => f.Length))
        {
            var current = folder;
            while (IsEmpty(current))
            {
                if (!TryRemove(current, toRecycleBin)) break;
                removed.Add(current);
                current = Path.GetDirectoryName(current) ?? string.Empty;
                if (current.Length == 0) break;
            }
        }

        return removed;
    }

    private static bool TryRemove(string folder, bool toRecycleBin)
    {
        try
        {
            if (toRecycleBin && FileDeleter.TryRecycleDirectory(folder)) return true;
            Directory.Delete(folder, recursive: false);
            return true;
        }
        catch { return false; }
    }
}
