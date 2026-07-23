using System.Runtime.InteropServices;

namespace MediaCatalog.Core.Scanning;

/// <summary>A drive/root available to scan.</summary>
public record ScanRoot(string Path, string Label, long TotalBytes, long FreeBytes)
{
    public string Display => string.IsNullOrWhiteSpace(Label)
        ? Path
        : $"{Path}  ({Label})";
}

/// <summary>
/// Walks the file system safely: enumerates every readable directory under a set of
/// roots and yields media files, silently skipping folders we can't access.
/// </summary>
public static class DriveScanner
{
    /// <summary>Enumerate fixed/removable drives that are ready to read.</summary>
    public static IReadOnlyList<ScanRoot> GetAvailableDrives()
    {
        var roots = new List<ScanRoot>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady) continue;
                if (d.DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Network))
                    continue;
                roots.Add(new ScanRoot(d.RootDirectory.FullName, d.VolumeLabel,
                    d.TotalSize, d.AvailableFreeSpace));
            }
            catch (IOException) { /* drive vanished mid-enumeration */ }
            catch (UnauthorizedAccessException) { }
        }
        return roots;
    }

    /// <summary>
    /// Depth-first walk yielding full paths of media files under <paramref name="roots"/>.
    /// Inaccessible directories are skipped rather than aborting the whole scan.
    /// </summary>
    public static IEnumerable<string> EnumerateMediaFiles(
        IEnumerable<string> roots, CancellationToken ct = default)
    {
        var pending = new Stack<string>(roots);
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = pending.Pop();

            string[] subDirs;
            try
            {
                subDirs = Directory.GetDirectories(dir);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or DirectoryNotFoundException)
            {
                continue;
            }
            foreach (var sub in subDirs)
            {
                // Skip reparse points (symlinks/junctions) to avoid loops.
                try
                {
                    var attrs = File.GetAttributes(sub);
                    if (attrs.HasFlag(FileAttributes.ReparsePoint)) continue;
                }
                catch { continue; }
                pending.Push(sub);
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(dir);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or DirectoryNotFoundException)
            {
                continue;
            }

            foreach (var file in files)
            {
                var ext = Path.GetExtension(file);
                if (MediaExtensions.IsMedia(ext) || MediaExtensions.IsIncompleteMarker(ext))
                    yield return file;
            }
        }
    }
}
