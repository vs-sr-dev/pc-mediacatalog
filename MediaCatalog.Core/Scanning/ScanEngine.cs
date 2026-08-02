using System.Diagnostics;
using MediaCatalog.Core.Classification;
using MediaCatalog.Core.Hashing;
using MediaCatalog.Core.Models;
using MediaCatalog.Core.Storage;

namespace MediaCatalog.Core.Scanning;

public record ScanProgress(int Done, int Total, string CurrentFile, string Phase);

/// <param name="MissingFiles">Enumerated, but gone by the time we got to them.</param>
/// <param name="UnhashedFiles">
/// Present, but hashing failed — locked, unreadable or refused. Duplicate detection is
/// blind to these, so they are handed back for the user to deal with.
/// </param>
/// <param name="SkippedBySize">Files outside the configured size limits.</param>
/// <param name="UnavailableRoots">
/// Drives that were never seen: not attached when the scan started, and still not attached
/// when it finished. Nothing under them was touched, in the catalogue or on disk.
/// </param>
public record ScanReport(
    List<string> MissingFiles,
    List<MediaFile>? UnhashedFiles = null,
    int SkippedBySize = 0,
    List<string>? UnavailableRoots = null)
{
    public IReadOnlyList<MediaFile> Unhashed => UnhashedFiles ?? new List<MediaFile>();
    public IReadOnlyList<string> Unavailable => UnavailableRoots ?? new List<string>();
}

/// <summary>
/// Orchestrates a full scan: enumerate media under the chosen roots, classify each
/// file, hash it (reusing prior hashes when the file is unchanged) and merge the
/// results into the catalogue. Reports determinate progress to the UI.
///
/// Designed to be interruptible and resumable: it checkpoints the catalogue to disk
/// periodically (via <c>onCheckpoint</c>) so a pause — or a crash — never loses hashing
/// work, and re-running over the same roots skips already-hashed files.
///
/// A root that is not attached is treated as unknown rather than empty. Its catalogue
/// entries are left alone, and — when asked to wait — the scan watches for the drive to
/// appear and picks it up, so an external drive plugged in halfway through still gets done.
/// </summary>
public class ScanEngine
{
    private readonly Catalog _catalog;

    /// <summary>How often the scan looks to see whether a missing drive has turned up.</summary>
    private static readonly TimeSpan RootPollInterval = TimeSpan.FromSeconds(5);

    public ScanEngine(Catalog catalog) => _catalog = catalog;

    /// <summary>True when a root can be read right now.</summary>
    public static bool IsRootAvailable(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return false;
        try { return Directory.Exists(root); }
        catch { return false; }
    }

    /// <summary>
    /// The roots in <paramref name="roots"/> that are not currently attached, so the
    /// caller can ask what to do about them before any work starts.
    /// </summary>
    public static List<string> UnavailableRoots(IEnumerable<string> roots) =>
        roots.Where(r => !IsRootAvailable(r)).ToList();

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
    /// the roots are authoritative — but only within the roots actually walked. Set false
    /// when scanning one folder into an existing catalogue: everything outside that folder
    /// is simply not in scope.
    /// </param>
    /// <param name="waitForMissingRoots">
    /// When true, the scan does not finish while a chosen drive is still unattached: it
    /// waits, watching for it, and scans it as soon as it appears. Cancel stops the wait.
    /// </param>
    /// <param name="probeMedia">
    /// Reads each file's length and quality as it is catalogued. Supplied by the caller,
    /// which owns the external tools; null leaves those columns to be filled in later by an
    /// analysis run or by verifying a single file.
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
        bool waitForMissingRoots = false,
        Func<MediaFile, CancellationToken, Task>? probeMedia = null,
        CancellationToken ct = default)
    {
        settings ??= new AppSettings();
        var enumPath = AppPaths.EnumerationPath;
        var missing = new List<string>();
        var unhashed = new List<MediaFile>();
        var outOfScope = new List<string>();
        var skippedBySize = 0;

        // A drive that is not plugged in has not become empty. Its files are left exactly
        // as they are — walked round, never pruned — until it turns up.
        var absent = UnavailableRoots(roots);
        var walked = roots.Where(IsRootAvailable).ToList();

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
            paths = await EnumerateAsync(walked, settings, ct);
            SaveEnumeration();
        }

        // Entries whose file is no longer where we left it: a file that turns up under a
        // new name with the same size and timestamp is that file, renamed.
        PrepareMoveDetection(paths);

        var total = paths.Count;
        var interval = checkpointInterval ?? TimeSpan.FromSeconds(30);
        var sw = Stopwatch.StartNew();
        var nextCheckpoint = interval;

        // Paths a resumed enumeration lists on a drive that is not attached this time.
        // Kept with their index so a checkpoint can point back at the earliest one.
        var deferred = new List<(int Index, string Path)>();

        for (var i = startIndex; i < total; i++)
        {
            // Cancellation is checked *before* mutating state, so a pause always stops at
            // a clean file boundary with the catalogue in a consistent state.
            ct.ThrowIfCancellationRequested();
            var path = paths[i];

            // Report progress as the count of *completed* files (i), so if we pause during
            // this file, the persisted resume index redoes it rather than skipping it.
            progress?.Report(new ScanProgress(i, total, Path.GetFileName(path), "Hashing & classifying"));

            if (absent.Count > 0 && UnderAny(path, absent))
            {
                deferred.Add((i, path));
                continue;
            }

            await ProcessFileAsync(path);
            Checkpoint(i + 1);
        }

        // Anything still on an unattached drive. Waiting is a deliberate choice made
        // before the scan started; without it the drive is simply reported as unseen.
        if (waitForMissingRoots)
            await WaitForAbsentRootsAsync();

        // Full pass completed. The enumeration snapshot (minus anything that turned out to
        // be missing) is the authoritative set of files that exist, so pruning here is
        // correct even across a restart. A partial/paused pass never reaches this point.
        if (pruneMissing)
        {
            var existing = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
            foreach (var m in missing) existing.Remove(m);
            foreach (var s in outOfScope) existing.Remove(s);
            foreach (var (_, path) in deferred) existing.Remove(path);

            // Only the ground actually covered is authoritative. A scan of C: says nothing
            // about what is on D:, and a drive that never turned up says nothing at all —
            // so pruning is confined to the roots that were walked. A filtered scan is
            // likewise only authoritative about the kind it went looking for, which is what
            // lets an audio scan and a video scan build one combined catalogue between them.
            _catalog.Files.RemoveAll(f =>
                !existing.Contains(f.FullPath) &&
                settings.IsExtensionScanned(f.Extension) &&
                UnderAny(f.FullPath, walked));
        }
        _catalog.RebuildIndex();

        // These can only run once every file is known: identical files share what each of
        // them worked out, and specials attach to the show or film they belong to.
        Classification.DuplicateMetadata.Propagate(_catalog.Files);
        ExtraLinker.Link(_catalog.Files);
        _catalog.LastScanUtc = DateTime.UtcNow;

        EnumerationCache.Clear(enumPath);
        progress?.Report(new ScanProgress(total, total, string.Empty, "Done"));

        // Only report files still in the catalogue: one that vanished mid-scan is a
        // missing file, not an unhashable one.
        var stillHere = unhashed.Where(f => !f.HasHash && _catalog.ByPath.ContainsKey(f.FullPath)).ToList();
        return new ScanReport(missing, stillHere, skippedBySize, absent);

        // --- the work itself, shared by the main pass and the late-arriving drives ---

        async Task ProcessFileAsync(string path)
        {
            // A file that vanished since enumeration must never abort the scan. Ones under
            // a "Temp" folder are ignored silently; the rest are reported afterwards.
            if (!File.Exists(path))
            {
                if (!DriveScanner.IsUnderTempFolder(path)) missing.Add(path);
                return;
            }

            FileInfo info;
            try { info = new FileInfo(path); }
            catch
            {
                if (!DriveScanner.IsUnderTempFolder(path)) missing.Add(path);
                return;
            }

            // Outside the configured size limits: not catalogued, and dropped if a
            // previous scan (with different limits) had picked it up.
            if (!settings.IsSizeInRange(info.Length))
            {
                skippedBySize++;
                outOfScope.Add(path);
                return;
            }

            var entry = MergeEntry(info);
            MediaClassifier.Classify(entry, settings);
            Classification.DuplicateMetadata.ApplyFolderTitle(entry, settings);
            QuickIntegrityCheck(entry, info);

            // Hash only when needed (new file or content changed). Skip obvious junk.
            if (entry.Integrity != IntegrityStatus.IncompleteDownload &&
                info.Length > 0 &&
                !entry.HasHash)
            {
                entry.Sha256 = await FileHasher.ComputeSha256Async(path, ct);

                // A file we could read the size of but not the contents: locked by another
                // program, or refused. Duplicate detection can't see it, so it is collected
                // and reported rather than quietly left out.
                entry.HashFailed = !entry.HasHash;
                if (entry.HashFailed) unhashed.Add(entry);
            }

            // How long it runs and how good it is. Read from the container header, which is
            // cheap next to hashing the file — and only for entries that don't know yet, so
            // re-scanning a library it has already measured costs nothing.
            if (probeMedia != null && Integrity.MediaProbe.NeedsProbe(entry))
            {
                try { await probeMedia(entry, ct); }
                catch (OperationCanceledException) { throw; }
                catch { /* a file the prober choked on is not a reason to stop the scan */ }
            }

            entry.IndexedUtc = DateTime.UtcNow;
            entry.FeatureVersion = CatalogRefresher.CurrentFeatureVersion;
        }

        // Periodic crash-safe checkpoint; hands back the index to resume from.
        void Checkpoint(int index)
        {
            if (onCheckpoint == null || sw.Elapsed < nextCheckpoint) return;
            _catalog.RebuildIndex();
            // Never point past work still owed on a drive we skipped over: resuming a
            // little early only re-walks files that are already hashed, which is cheap.
            var resumeAt = deferred.Count > 0 ? Math.Min(index, deferred[0].Index) : index;
            try { onCheckpoint(resumeAt); }
            catch { /* a failed checkpoint must not abort the scan */ }
            nextCheckpoint = sw.Elapsed + interval;
        }

        void SaveEnumeration() => new EnumerationCache
        {
            Roots = roots.ToList(),
            Paths = paths,
            CreatedUtc = DateTime.UtcNow
        }.Save(enumPath);

        // Wait for each unattached drive and scan it the moment it appears.
        async Task WaitForAbsentRootsAsync()
        {
            var done = total;
            while (absent.Count > 0)
            {
                ct.ThrowIfCancellationRequested();

                var arrived = absent.Where(IsRootAvailable).ToList();
                if (arrived.Count == 0)
                {
                    progress?.Report(new ScanProgress(done, Math.Max(done, total), string.Empty,
                        $"Waiting for {string.Join(", ", absent)} — connect the drive, or Cancel to stop"));
                    await Task.Delay(RootPollInterval, ct);
                    continue;
                }

                foreach (var root in arrived)
                {
                    absent.Remove(root);
                    walked.Add(root);

                    // Either the resumed enumeration already listed this drive's files, or
                    // it was skipped at enumeration time and has to be walked now.
                    var pending = deferred.Where(d => IsUnder(d.Path, root)).ToList();
                    if (pending.Count > 0) deferred.RemoveAll(d => IsUnder(d.Path, root));
                    else
                    {
                        progress?.Report(new ScanProgress(done, total, string.Empty,
                            $"Enumerating {root}…"));
                        foreach (var found in await EnumerateAsync(new[] { root }, settings, ct))
                        {
                            paths.Add(found);
                            pending.Add((paths.Count - 1, found));
                        }
                        total = paths.Count;
                        SaveEnumeration();
                    }

                    foreach (var (index, path) in pending)
                    {
                        ct.ThrowIfCancellationRequested();
                        progress?.Report(new ScanProgress(done, total, Path.GetFileName(path),
                            $"Hashing & classifying ({root})"));
                        await ProcessFileAsync(path);
                        done++;
                        Checkpoint(index + 1);
                    }
                }
            }
        }
    }

    /// <summary>Walk roots for media files, honouring exclusions and the scan's media filter.</summary>
    private static Task<List<string>> EnumerateAsync(
        IReadOnlyList<string> roots, AppSettings settings, CancellationToken ct) =>
        Task.Run(() => DriveScanner.EnumerateMediaFiles(
            roots, ct,
            excludeDescent: settings.IsDescentBlocked,
            // An audio-only or video-only scan drops the other kind here, before any work
            // is done on it. What it skips stays in the catalogue untouched, so running one
            // kind and then the other builds a single combined catalogue.
            ignoreExtension: ext => settings.IsExtensionIgnored(ext) ||
                                    !settings.IsExtensionScanned(ext)).ToList(), ct);

    private static bool UnderAny(string path, IEnumerable<string> roots) =>
        roots.Any(r => IsUnder(path, r));

    private static bool IsUnder(string path, string root)
    {
        var trimmed = root.TrimEnd('\\', '/');
        if (trimmed.Length == 0) return false;
        return path.StartsWith(trimmed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith(trimmed + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Catalogue entries whose file is missing from this scan, indexed by size and
    /// timestamp, so a renamed file can be recognised as the one that vanished.
    /// </summary>
    private Dictionary<(long Size, DateTime Modified), List<MediaFile>> _possiblyMoved = new();

    private void PrepareMoveDetection(IReadOnlyCollection<string> paths)
    {
        _possiblyMoved = new Dictionary<(long, DateTime), List<MediaFile>>();
        var seen = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);

        foreach (var file in _catalog.Files)
        {
            // Only entries worth carrying: still where they were, or never hashed, and
            // there is nothing to save by matching them up.
            if (seen.Contains(file.FullPath) || !file.HasHash) continue;

            var key = (file.SizeBytes, file.LastModifiedUtc);
            if (_possiblyMoved.TryGetValue(key, out var list)) list.Add(file);
            else _possiblyMoved[key] = new List<MediaFile> { file };
        }
    }

    /// <summary>
    /// The catalogue entry for a file that has been renamed or moved to
    /// <paramref name="info"/>, or null. Only an unambiguous match counts: if two missing
    /// files share a size and timestamp, we cannot tell which one this is, and a wrong
    /// guess would attach the wrong hash.
    /// </summary>
    private MediaFile? TakeMovedEntry(FileInfo info)
    {
        var key = (info.Length, info.LastWriteTimeUtc);
        if (!_possiblyMoved.TryGetValue(key, out var candidates) || candidates.Count != 1)
            return null;

        var entry = candidates[0];
        _possiblyMoved.Remove(key);
        _catalog.ByPath.Remove(entry.FullPath);
        return entry;
    }

    /// <summary>
    /// Find or create the catalogue entry for a path, refreshing basic attributes.
    /// If the file changed size/date since last scan, invalidate the stored hash.
    /// </summary>
    private MediaFile MergeEntry(FileInfo info)
    {
        if (!_catalog.ByPath.TryGetValue(info.FullName, out var entry))
        {
            // A renamed file keeps everything already known about it — hash included, so
            // it is still recognised as a duplicate of its copies without being re-read.
            entry = TakeMovedEntry(info);
            if (entry == null)
            {
                entry = new MediaFile { FullPath = info.FullName };
                _catalog.Files.Add(entry);
            }
            else
            {
                entry.FullPath = info.FullName;
            }
            _catalog.ByPath[info.FullName] = entry;
        }

        var modifiedUtc = info.LastWriteTimeUtc;
        var changed = entry.SizeBytes != info.Length || entry.LastModifiedUtc != modifiedUtc;
        if (changed)
        {
            entry.Sha256 = string.Empty;
            entry.HashFailed = false;   // a changed file earns a fresh attempt
        }

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
