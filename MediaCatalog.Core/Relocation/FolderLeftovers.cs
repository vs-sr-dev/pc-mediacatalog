namespace MediaCatalog.Core.Relocation;

/// <summary>
/// A source folder a consolidation has left behind, and what is still in it.
/// </summary>
/// <param name="Bytes">Everything still in the folder and below it, added up.</param>
/// <param name="Files">How many files that is.</param>
/// <param name="Protected">
/// Catalogued files in there that have <em>not</em> been filed yet. One of these is the
/// whole reason the folder cannot go: it is not a scrap, it is work outstanding.
/// </param>
public record LeftoverFolder(
    string Path, long Bytes, int Files, IReadOnlyList<string> Protected, IReadOnlyList<string> Examples)
{
    /// <summary>True when nothing at all is left — the case every earlier version handled.</summary>
    public bool IsEmpty => Files == 0;

    /// <summary>A line for the confirmation dialog.</summary>
    public string Describe() =>
        IsEmpty
            ? Path + "   (empty)"
            : $"{Path}   ({Files} file(s), {Bytes / 1024.0 / 1024.0:0.#} MB left)";
}

/// <summary>
/// Decides which folders a consolidation may take away with it.
///
/// An empty folder is easy and was always offered. The harder case is the folder that is
/// *nearly* empty: a film moved out of its download folder leaves a sample clip, a screenshot
/// and a readme behind, and those are litter — but the same three megabytes sitting in a music
/// folder are very probably a track somebody wants. So the limit is set per category by the
/// user, and the folder only goes when what is left falls under it.
///
/// One thing overrides the size entirely: a catalogued file that has not been consolidated
/// yet. However small it is, that is work the user has not finished, and a folder holding
/// one is never offered however far under the limit it falls.
/// </summary>
public static class FolderLeftovers
{
    /// <summary>
    /// Look over the folders files have just left, and report the ones holding little enough
    /// to be taken away. Deepest first, so removing one can make its parent a candidate too.
    /// </summary>
    /// <param name="thresholdBytes">
    /// How much may be left before the folder is worth keeping. 0 means only a genuinely
    /// empty folder is ever reported, which is what every version before this one did.
    /// </param>
    /// <param name="isUnfinished">
    /// True for a path the catalogue knows about and has not filed. These are never
    /// disturbed, and their presence protects the whole folder.
    /// </param>
    /// <param name="isKept">
    /// Folders the user has named somewhere — a download folder they scan, a folder they
    /// watch, a consolidation root. Emptying one is not a reason to take it away: they told
    /// us it matters, and it will be wanted again the next time something lands in it.
    /// </param>
    public static List<LeftoverFolder> Find(
        IEnumerable<string> folders, long thresholdBytes, Func<string, bool> isUnfinished,
        Func<string, bool>? isKept = null)
    {
        var results = new List<LeftoverFolder>();

        foreach (var folder in folders
                     .Where(f => !string.IsNullOrWhiteSpace(f))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderByDescending(f => f.Length))
        {
            if (!Directory.Exists(folder)) continue;
            if (IsDriveRoot(folder)) continue;
            if (isKept?.Invoke(folder) == true) continue;

            if (Measure(folder, thresholdBytes, isUnfinished) is { } leftover)
                results.Add(leftover);
        }

        return results;
    }

    /// <summary>
    /// What is in a folder, or null when there is too much of it — either more bytes than the
    /// threshold allows or a catalogued file still waiting to be filed.
    /// </summary>
    private static LeftoverFolder? Measure(
        string folder, long thresholdBytes, Func<string, bool> isUnfinished)
    {
        long bytes = 0;
        var count = 0;
        var unfinished = new List<string>();
        var examples = new List<string>();

        string[] files;
        try { files = Directory.GetFiles(folder, "*", SearchOption.AllDirectories); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }

        foreach (var path in files)
        {
            count++;

            if (isUnfinished(path))
            {
                unfinished.Add(path);
                // Worth knowing about, but there is nothing to weigh up any more: the
                // folder stays whatever else is in it.
                if (unfinished.Count >= 10) break;
                continue;
            }

            try { bytes += new FileInfo(path).Length; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

            if (examples.Count < 6) examples.Add(Path.GetFileName(path));
        }

        if (unfinished.Count > 0) return null;
        if (count > 0 && bytes > thresholdBytes) return null;

        return new LeftoverFolder(folder, bytes, count, unfinished, examples);
    }

    /// <summary>
    /// Take the folders away, contents and all, then any parent they leave empty in turn —
    /// a season folder going often leaves the show folder with nothing in it either.
    /// </summary>
    /// <param name="toRecycleBin">
    /// Recycle rather than delete outright. Deleting is the sensible default here in a way
    /// it never is for a file: what is going has already been judged to be scraps.
    /// </param>
    /// <param name="isKept">
    /// Folders that stay whatever happens — the ones the user has configured. A download
    /// folder is empty most of the time; that is what it is for, and deleting it the moment
    /// the last thing in it was filed would be a poor reward for tidying up.
    /// </param>
    public static List<string> Remove(
        IEnumerable<string> folders, bool toRecycleBin, Func<string, bool>? isKept = null)
    {
        var removed = new List<string>();

        foreach (var folder in folders.OrderByDescending(f => f.Length))
        {
            if (isKept?.Invoke(folder) == true) continue;
            if (!TryRemove(folder, toRecycleBin)) continue;
            removed.Add(folder);

            // Parents only when they are genuinely empty: a parent still holding files was
            // never measured against the threshold and is not ours to judge.
            var parent = Path.GetDirectoryName(folder) ?? string.Empty;
            while (parent.Length > 0 && EmptyFolderCleaner.IsEmpty(parent))
            {
                if (isKept?.Invoke(parent) == true) break;
                if (!TryRemove(parent, toRecycleBin)) break;
                removed.Add(parent);
                parent = Path.GetDirectoryName(parent) ?? string.Empty;
            }
        }

        return removed;
    }

    private static bool TryRemove(string folder, bool toRecycleBin)
    {
        try
        {
            if (!Directory.Exists(folder)) return false;
            if (IsDriveRoot(folder)) return false;

            if (toRecycleBin && FileDeleter.TryRecycleDirectory(folder)) return true;
            Directory.Delete(folder, recursive: true);
            return true;
        }
        catch { return false; }
    }

    private static bool IsDriveRoot(string folder)
    {
        try
        {
            var root = Path.GetPathRoot(folder);
            return string.Equals(folder.TrimEnd('\\', '/'), root?.TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
