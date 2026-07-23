using System.Security.Cryptography;

namespace MediaCatalog.Core.Hashing;

/// <summary>Computes SHA-256 content hashes used for exact-duplicate detection.</summary>
public static class FileHasher
{
    /// <summary>
    /// Streams the file through SHA-256 so even large videos hash with a small,
    /// constant memory footprint. Returns lower-case hex, or empty on failure.
    /// </summary>
    public static async Task<string> ComputeSha256Async(string path, CancellationToken ct = default)
    {
        try
        {
            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 1 << 20, useAsync: true);
            using var sha = SHA256.Create();
            var hash = await sha.ComputeHashAsync(stream, ct);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }
}
