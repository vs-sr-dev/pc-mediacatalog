using System.Diagnostics;
using MediaCatalog.Core.Classification;
using MediaCatalog.Core.Hashing;
using MediaCatalog.Core.Models;
using MediaCatalog.Core.Storage;

namespace MediaCatalog.Core.Scanning;

public record ScanProgress(int Done, int Total, string CurrentFile, string Phase);

/// <summary>Outcome of a completed scan; currently the files that could not be found.</summary>
public record ScanReport(List<string> MissingFiles);

/// <summary>
/// Orchestrates a full scan: enumerate media under the chosen roots, classify each
/// file, hash it (reusing prior hashes when the file is unchanged) and merge the
/// results into the catalogue. Reports determinate progress to the UI.
///
/// Designed to be interruptible and resumable: it checkpoints the catalogue to disk
/// periodically (via <paramref name="onCheckpoint"/>) so a pause — or a crash — never
/// loses hashing work, and re-running over the same roots skips already-hashed files.
/// </summary>
public class ScanEngine
{
    private readonly Catalog _catalog;

    public ScanEngine(Catalog catalog) => _catalog = catalog;

    /// <summary>
    /// Scan <paramref name="roots"/>. When cancelled (pause/cancel) the method throws
    /// <see cref="OperationCanceledException"/> having preserved all work done so far;
    /// entries for vanished files are only pruned once a full pass completes.
    /// </summary>
    /// <param name="onCheckpoint">
    /// Called periodically (and carries the resume index = files processed so far) so the
    /// caller can persist the catalogue and scan session for crash-safe resume.
    /// </param>
    /// <param name="resume">
    /// When true, reuse the on-disk enumeration snapshot (if it matches the roots) and
    /// continue from <paramref name="resumeFromIndex"/> instead of re-walking the drives.
    /// </param>
    /// <param name="pruneMissing">
    /// When true (a full drive scan) catalogue entries that were not seen are removed, as
    /// the roots are authoritative. Set false when scanning one folder into an existing
    /// catalogue: everything outside that folder is simply not in scope.
    /// </param>
    public async Task<ScanReport> ScanAsync(
        IReadOnlyList<string> roots,
        IProgress<ScanProgress>? progress = null,
        Action<int>? onCheckpoint = null,
        TimeSpan? checkpointInterval = null,
        bool resume = false,
        int resumeFromIndex = 0,
        AppSettings? settings = null,
        bool pruneMissing = true,
        CancellationToken ct = default)
    {
        settings ??= new AppSettings();
        var enumPath = AppPaths.EnumerationPath;
        var missing = new List<string>();

        // Reuse a matching enumeration snapshot on resume; otherwise walk the drives,
        // pruning excluded folders and ignored file types as we go.
        List<string> paths;
        var startIndex = 0;
        var cache = resume ? EnumerationCache.Load(enumPath) : null;
        if (cache != null && cache.MatchesRoots(roots))
        {
            paths = cache.Paths;
            startIndex = Math.Clamp(resumeFromIndex, 0, paths.Count);
            progress?.Report(new ScanProgress(startIndex, paths.Count, string.Empty,
                "Resuming from saved enumeration…"));
        }
        else
        {
            progress?.Report(new ScanProgress(0, 0, string.Empty, "Enumerating files…"));
            paths = await Task.Run(() => DriveScanner.EnumerateMediaFiles(
                roots, ct,
                excludeDescent: settings.IsDescentBlocked,
                ignoreExtension: settings.IsExtensionIgnored).ToList(), ct);
            new EnumerationCache
            {
                Roots = roots.ToList(),
                Paths = paths,
                CreatedUtc = DateTime.UtcNow
            }.Save(enumPath);
        }

        var total = paths.Count;
        var interval = checkpointInterval ?? TimeSpan.FromSeconds(30);
        var sw = Stopwatch.StartNew();
        var nextCheckpoint = interval;

        for (var i = startIndex; i < total; i++)
        {
            // Cancellation is checked *before* mutating state, so a pause always stops at
            // a clean file boundary with the catalogue in a consistent state.
            ct.ThrowIfCancellationRequested();
            var path = paths[i];

            // Report progress as the count of *completed* files (i), so if we pause during
            // this file, the persisted resume index redoes it rather than skipping it.
            progress?.Report(new ScanProgress(i, total, Path.GetFileName(path), "Hashing & classifying"));

            // A file that vanished since enumeration must never abort the scan. Ones under
            // a "Temp" folder are ignored silently; the rest are reported afterwards.
            if (!File.Exists(path))
            {
                if (!DriveScanner.IsUnderTempFolder(path)) missing.Add(path);
                continue;
            }

            FileInfo info;
            try { info = new FileInfo(path); }
            catch
            {
                if (!DriveScanner.IsUnderTempFolder(path)) missing.Add(path);
                continue;
            }

            var entry = MergeEntry(info);
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
            entry.FeatureVersion = CatalogRefresher.CurrentFeatureVersion;

            // Periodic crash-safe checkpoint; hand back the resume index (i + 1).
            if (onCheckpoint != null && sw.Elapsed >= nextCheckpoint)
            {
                _catalog.RebuildIndex();
                TryCheckpoint(onCheckpoint, i + 1);
                nextCheckpoint = sw.Elapsed + interval;
            }
        }

        // Full pass completed. The enumeration snapshot (minus anything that turned out to
        // be missing) is the authoritative set of files that exist, so pruning here is
        // correct even across a restart. A partial/paused pass never reaches this point.
        if (pruneMissing)
        {
            var existing = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
            foreach (var m in missing) existing.Remove(m);
            _catalog.Files.RemoveAll(f => !existing.Contains(f.FullPath));
        }
        _catalog.RebuildIndex();

        // Specials/featurettes can only be attached once every file is known.
        ExtraLinker.Link(_catalog.Files);
        _catalog.LastScanUtc = DateTime.UtcNow;

        EnumerationCache.Clear(enumPath);
        progress?.Report(new ScanProgress(total, total, string.Empty, "Done"));
        return new ScanReport(missing);
    }

    private static void TryCheckpoint(Action<int> onCheckpoint, int index)
    {
        try { onCheckpoint(index); }
        catch { /* a failed checkpoint must not abort the scan */ }
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
