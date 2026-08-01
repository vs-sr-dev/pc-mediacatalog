using MediaCatalog.Core.Hashing;
using MediaCatalog.Core.Models;

namespace MediaCatalog.Core.Relocation;

/// <summary>What to do when the destination name is already taken.</summary>
public enum DuplicatePolicy
{
    /// <summary>File it beside the existing one as "name (1).ext" (default).</summary>
    Rename,
    /// <summary>Leave the destination alone and report back.</summary>
    Skip
}

/// <param name="AlreadyPresent">
/// The identical file is already at the destination, so nothing was copied — the caller
/// can offer to delete the redundant source copy.
/// </param>
public record RelocationResult(bool Success, string Message, string NewPath, bool AlreadyPresent = false);

/// <summary>
/// Moves files safely: copy to the destination, verify the copy hashes identically
/// to the source, and only then (optionally) delete the original. A failed verify
/// leaves the original untouched.
/// </summary>
public static class FileRelocator
{
    /// <param name="newFileName">Rename on arrival (e.g. an episode-numbered name); null keeps the current name.</param>
    /// <param name="onDuplicate">What to do when something already occupies the destination name.</param>
    /// <param name="copiedBytes">Reports bytes as they are copied, for progress and ETA.</param>
    public static async Task<RelocationResult> RelocateAsync(
        MediaFile file,
        string destinationDir,
        bool deleteOriginal,
        string? newFileName = null,
        DuplicatePolicy onDuplicate = DuplicatePolicy.Rename,
        IProgress<long>? copiedBytes = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(file.FullPath))
            return new RelocationResult(false, "Source file no longer exists.", file.FullPath);

        try
        {
            Directory.CreateDirectory(destinationDir);
            var desired = Path.Combine(destinationDir,
                string.IsNullOrWhiteSpace(newFileName) ? file.FileName : newFileName);

            // Moving within one volume is a rename: the file's data never moves, so a
            // terabyte lands as fast as a byte. Taken *before* any hashing, because
            // hashing a large file costs about as much as copying it would — which is
            // exactly the wait this path exists to avoid. Nothing is duplicated and the
            // original is never deleted, so there is no copy to verify.
            var sameVolume = deleteOriginal && VolumeInfo.SameVolume(file.FullPath, destinationDir);
            if (sameVolume && !File.Exists(desired))
            {
                if (PathsEqual(desired, file.FullPath))
                    return new RelocationResult(true, "Already in place.", file.FullPath, AlreadyPresent: true);

                if (TryRename(file.FullPath, desired))
                {
                    copiedBytes?.Report(file.SizeBytes);
                    UpdateFile(file, desired, file.Sha256);
                    return new RelocationResult(true, "Moved on the same drive (no copy needed).", desired);
                }
            }

            // Ensure we have a trustworthy source hash to verify against.
            var sourceHash = file.HasHash
                ? file.Sha256
                : await FileHasher.ComputeSha256Async(file.FullPath, ct);
            if (string.IsNullOrEmpty(sourceHash))
                return new RelocationResult(false, "Could not read source to hash it.", file.FullPath);

            // Never make a second copy of something that is already in the library.
            if (File.Exists(desired) && !PathsEqual(desired, file.FullPath))
            {
                if (await IsSameContentAsync(desired, sourceHash, file.SizeBytes, ct))
                    return new RelocationResult(false,
                        "Already present at the destination.", desired, AlreadyPresent: true);

                if (onDuplicate == DuplicatePolicy.Skip)
                    return new RelocationResult(false,
                        "A different file already uses that name at the destination.", desired);
            }

            var destPath = MakeUniquePath(desired);
            if (PathsEqual(destPath, file.FullPath))
                return new RelocationResult(true, "Already in place.", file.FullPath, AlreadyPresent: true);

            // The name was taken, so the fast path above stood down; now that a free name
            // has been chosen, rename into it rather than copying a whole file for nothing.
            if (sameVolume && TryRename(file.FullPath, destPath))
            {
                copiedBytes?.Report(file.SizeBytes);
                UpdateFile(file, destPath, sourceHash);
                return new RelocationResult(true, "Moved on the same drive (no copy needed).", destPath);
            }

            await CopyAsync(file.FullPath, destPath, copiedBytes, ct);

            var copyHash = await FileHasher.ComputeSha256Async(destPath, ct);
            if (!string.Equals(copyHash, sourceHash, StringComparison.OrdinalIgnoreCase))
            {
                FileDeleter.TryDeleteQuietly(destPath); // roll back the bad copy
                return new RelocationResult(false,
                    "Verification failed: copy did not match source. Original kept.", file.FullPath);
            }

            if (deleteOriginal)
            {
                // Through the shared deleter, so a read-only original is dealt with the
                // same way here as anywhere else rather than simply refusing.
                var failure = FileDeleter.DeleteOne(file.FullPath, toRecycleBin: false);
                if (failure != null)
                {
                    // Copy is verified and safe; we just couldn't remove the original.
                    UpdateFile(file, destPath, sourceHash);
                    return new RelocationResult(true,
                        $"Copied and verified, but the original could not be deleted: {failure.Reason}",
                        destPath);
                }
            }

            UpdateFile(file, destPath, sourceHash);
            return new RelocationResult(true,
                deleteOriginal ? "Moved and verified." : "Copied and verified.", destPath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new RelocationResult(false, $"Relocation failed: {ex.Message}", file.FullPath);
        }
    }

    /// <summary>
    /// Rename the file into place. Returns false — without throwing — if the filesystem
    /// won't do it (a junction crossing volumes, a permissions problem), leaving the
    /// caller to fall back on copy-and-verify.
    /// </summary>
    private static bool TryRename(string source, string destination)
    {
        try
        {
            File.Move(source, destination, overwrite: false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    private static void UpdateFile(MediaFile file, string newPath, string hash)
    {
        file.FullPath = newPath;
        file.FileName = Path.GetFileName(newPath);
        file.Sha256 = hash;
    }

    private static async Task CopyAsync(
        string source, string dest, IProgress<long>? copiedBytes, CancellationToken ct)
    {
        const int bufferSize = 1 << 20;
        await using var src = new FileStream(source, FileMode.Open, FileAccess.Read,
            FileShare.Read, bufferSize, useAsync: true);
        await using var dst = new FileStream(dest, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, bufferSize, useAsync: true);

        if (copiedBytes == null)
        {
            await src.CopyToAsync(dst, bufferSize, ct);
            return;
        }

        // Copy by hand so progress can be reported as it goes: on multi-gigabyte files a
        // per-file progress bar would sit still for minutes at a time.
        var buffer = new byte[bufferSize];
        int read;
        while ((read = await src.ReadAsync(buffer, ct)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, read), ct);
            copiedBytes.Report(read);
        }
    }

    /// <summary>
    /// True when the file at <paramref name="path"/> is the same content as the source:
    /// same length, and (when that matches) the same hash.
    /// </summary>
    private static async Task<bool> IsSameContentAsync(
        string path, string sourceHash, long sourceLength, CancellationToken ct)
    {
        try
        {
            var info = new FileInfo(path);
            if (sourceLength > 0 && info.Length != sourceLength) return false;
            var hash = await FileHasher.ComputeSha256Async(path, ct);
            return !string.IsNullOrEmpty(hash) &&
                   string.Equals(hash, sourceHash, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool PathsEqual(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    }

    /// <summary>Avoid clobbering an existing file: "name.mkv" -> "name (1).mkv".</summary>
    private static string MakeUniquePath(string desired)
    {
        if (!File.Exists(desired)) return desired;

        var dir = Path.GetDirectoryName(desired)!;
        var stem = Path.GetFileNameWithoutExtension(desired);
        var ext = Path.GetExtension(desired);
        for (var i = 1; ; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}
