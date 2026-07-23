using MediaCatalog.Core.Models;

namespace MediaCatalog.Core.Duplicates;

/// <summary>A set of files whose contents are byte-for-byte identical.</summary>
public class DuplicateGroup
{
    public string Sha256 { get; set; } = string.Empty;
    public List<MediaFile> Files { get; set; } = new();

    public long SizeBytes => Files.Count > 0 ? Files[0].SizeBytes : 0;

    /// <summary>Bytes that could be reclaimed by keeping a single copy.</summary>
    public long ReclaimableBytes => SizeBytes * Math.Max(0, Files.Count - 1);
}

/// <summary>
/// Groups exact duplicates by content hash. (Perceptual/fingerprint-based
/// near-duplicate detection across encodings is a later phase.)
/// </summary>
public static class DuplicateFinder
{
    public static List<DuplicateGroup> FindExactDuplicates(IEnumerable<MediaFile> files)
    {
        return files
            .Where(f => f.HasHash)
            .GroupBy(f => f.Sha256, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => new DuplicateGroup
            {
                Sha256 = g.Key,
                Files = g.OrderBy(f => f.FullPath).ToList()
            })
            .OrderByDescending(g => g.ReclaimableBytes)
            .ToList();
    }
}
