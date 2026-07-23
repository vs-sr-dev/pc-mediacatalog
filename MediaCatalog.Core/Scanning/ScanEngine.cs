using MediaCatalog.Core.Classification;
using MediaCatalog.Core.Hashing;
using MediaCatalog.Core.Models;

namespace MediaCatalog.Core.Scanning;

public record ScanProgress(int Done, int Total, string CurrentFile, string Phase);

/// <summary>
/// Orchestrates a full scan: enumerate media under the chosen roots, classify each
/// file, hash it (reusing prior hashes when the file is unchanged) and merge the
/// results into the catalogue. Reports determinate progress to the UI.
/// </summary>
public class ScanEngine
{
    private readonly Catalog _catalog;

    public ScanEngine(Catalog catalog) => _catalog = catalog;

    public async Task ScanAsync(
        IReadOnlyList<string> roots,
        IProgress<ScanProgress>? progress = null,
        CancellationToken ct = default)
    {
        progress?.Report(new ScanProgress(0, 0, string.Empty, "Enumerating files…"));

        // Materialise the file list first so we can show a real progress bar.
        var paths = await Task.Run(
            () => DriveScanner.EnumerateMediaFiles(roots, ct).ToList(), ct);

        var total = paths.Count;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var done = 0;

        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            seen.Add(path);

            FileInfo info;
            try { info = new FileInfo(path); }
            catch { done++; continue; }

            var entry = MergeEntry(info);
            progress?.Report(new ScanProgress(done, total, entry.FileName, "Hashing & classifying"));

            MediaClassifier.Classify(entry);
            QuickIntegrityCheck(entry, info);

            // Hash only when needed (new file or content changed). Skip obvious junk.
            if (entry.Integrity != IntegrityStatus.IncompleteDownload &&
                info.Length > 0 &&
                !entry.HasHash)
            {
                entry.Sha256 = await FileHasher.ComputeSha256Async(path, ct);
            }

            entry.IndexedUtc = DateTime.UtcNow;
            done++;
        }

        // Drop entries whose files disappeared since a previous scan.
        _catalog.Files.RemoveAll(f => !seen.Contains(f.FullPath));
        _catalog.RebuildIndex();
        _catalog.LastScanUtc = DateTime.UtcNow;

        progress?.Report(new ScanProgress(total, total, string.Empty, "Done"));
    }

    /// <summary>
    /// Find or create the catalogue entry for a path, refreshing basic attributes.
    /// If the file changed size/date since last scan, invalidate the stored hash.
    /// </summary>
    private MediaFile MergeEntry(FileInfo info)
    {
        if (!_catalog.ByPath.TryGetValue(info.FullName, out var entry))
        {
            entry = new MediaFile { FullPath = info.FullName };
            _catalog.Files.Add(entry);
            _catalog.ByPath[info.FullName] = entry;
        }

        var modifiedUtc = info.LastWriteTimeUtc;
        var changed = entry.SizeBytes != info.Length || entry.LastModifiedUtc != modifiedUtc;
        if (changed)
            entry.Sha256 = string.Empty;

        entry.FileName = info.Name;
        entry.Extension = info.Extension;
        entry.SizeBytes = info.Length;
        entry.LastModifiedUtc = modifiedUtc;
        return entry;
    }

    /// <summary>
    /// Cheap, no-decode integrity signals. Deep corruption checks via FFmpeg are a
    /// later phase; here we only catch the obvious cases.
    /// </summary>
    private static void QuickIntegrityCheck(MediaFile entry, FileInfo info)
    {
        if (MediaExtensions.IsIncompleteMarker(info.Extension))
            entry.Integrity = IntegrityStatus.IncompleteDownload;
        else if (info.Length == 0)
            entry.Integrity = IntegrityStatus.Corrupt;
        else
            entry.Integrity = IntegrityStatus.Ok;
    }
}
