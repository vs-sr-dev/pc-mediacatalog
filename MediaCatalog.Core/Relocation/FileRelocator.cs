using MediaCatalog.Core.Hashing;
using MediaCatalog.Core.Models;

namespace MediaCatalog.Core.Relocation;

public record RelocationResult(bool Success, string Message, string NewPath);

/// <summary>
/// Moves files safely: copy to the destination, verify the copy hashes identically
/// to the source, and only then (optionally) delete the original. A failed verify
/// leaves the original untouched.
/// </summary>
public static class FileRelocator
{
    public static async Task<RelocationResult> RelocateAsync(
        MediaFile file,
        string destinationDir,
        bool deleteOriginal,
        CancellationToken ct = default)
    {
        if (!File.Exists(file.FullPath))
            return new RelocationResult(false, "Source file no longer exists.", file.FullPath);

        try
        {
            Directory.CreateDirectory(destinationDir);
            var destPath = MakeUniquePath(Path.Combine(destinationDir, file.FileName));

            // Ensure we have a trustworthy source hash to verify against.
            var sourceHash = file.HasHash
                ? file.Sha256
                : await FileHasher.ComputeSha256Async(file.FullPath, ct);
            if (string.IsNullOrEmpty(sourceHash))
                return new RelocationResult(false, "Could not read source to hash it.", file.FullPath);

            await CopyAsync(file.FullPath, destPath, ct);

            var copyHash = await FileHasher.ComputeSha256Async(destPath, ct);
            if (!string.Equals(copyHash, sourceHash, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(destPath); // roll back the bad copy
                return new RelocationResult(false,
                    "Verification failed: copy did not match source. Original kept.", file.FullPath);
            }

            if (deleteOriginal)
            {
                try
                {
                    File.Delete(file.FullPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Copy is verified and safe; we just couldn't remove the original.
                    UpdateFile(file, destPath, sourceHash);
                    return new RelocationResult(true,
                        "Copied and verified, but the original could not be deleted.", destPath);
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

    private static void UpdateFile(MediaFile file, string newPath, string hash)
    {
        file.FullPath = newPath;
        file.FileName = Path.GetFileName(newPath);
        file.Sha256 = hash;
    }

    private static async Task CopyAsync(string source, string dest, CancellationToken ct)
    {
        const int bufferSize = 1 << 20;
        await using var src = new FileStream(source, FileMode.Open, FileAccess.Read,
            FileShare.Read, bufferSize, useAsync: true);
        await using var dst = new FileStream(dest, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, bufferSize, useAsync: true);
        await src.CopyToAsync(dst, bufferSize, ct);
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

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort */ }
    }
}
