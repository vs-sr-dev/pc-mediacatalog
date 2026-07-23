using System.Numerics;
using MediaCatalog.Core.Models;

namespace MediaCatalog.Core.Fingerprinting;

/// <summary>A set of files judged to be the same content despite different encodings.</summary>
public class NearDuplicateGroup
{
    public MediaKind Kind { get; set; }
    public List<MediaFile> Files { get; set; } = new();
    /// <summary>Lowest pairwise similarity within the group (0..1); higher = more confident.</summary>
    public double MinSimilarity { get; set; }
}

/// <summary>
/// Compares perceptual fingerprints and clusters near-duplicates. Similarity is a
/// 0..1 score; the defaults are deliberately conservative to limit false positives,
/// but matches should still be treated as candidates for a human to confirm.
/// </summary>
public static class FingerprintMatcher
{
    public const double AudioThreshold = 0.90;
    public const double VideoThreshold = 0.88;

    /// <summary>Chromaprint similarity: 1 − bit-error-rate over the aligned prefix.</summary>
    public static double AudioSimilarity(uint[] a, uint[] b)
    {
        var n = Math.Min(a.Length, b.Length);
        if (n < 10) return 0; // too little signal to trust
        long diffBits = 0;
        for (var i = 0; i < n; i++)
            diffBits += BitOperations.PopCount(a[i] ^ b[i]);
        return 1.0 - diffBits / (double)(n * 32);
    }

    /// <summary>Perceptual video similarity: 1 − average per-frame Hamming distance / 64.</summary>
    public static double VideoSimilarity(ulong[] a, ulong[] b)
    {
        var n = Math.Min(a.Length, b.Length);
        if (n == 0) return 0;
        long diffBits = 0;
        for (var i = 0; i < n; i++)
            diffBits += BitOperations.PopCount(a[i] ^ b[i]);
        return 1.0 - diffBits / (double)(n * 64);
    }

    /// <summary>
    /// Cluster files whose fingerprints match above the per-kind threshold. Only files
    /// that already carry a fingerprint participate; exact byte-duplicates naturally
    /// cluster here too but are better surfaced via the exact-duplicate view.
    /// </summary>
    public static List<NearDuplicateGroup> FindNearDuplicates(IEnumerable<MediaFile> files)
    {
        var groups = new List<NearDuplicateGroup>();
        groups.AddRange(Cluster(
            files.Where(f => f.Kind == MediaKind.Audio && !string.IsNullOrEmpty(f.AudioFingerprint)).ToList(),
            MediaKind.Audio));
        groups.AddRange(Cluster(
            files.Where(f => f.Kind == MediaKind.Video && !string.IsNullOrEmpty(f.VideoFingerprint)).ToList(),
            MediaKind.Video));
        return groups.OrderByDescending(g => g.Files.Count).ThenByDescending(g => g.MinSimilarity).ToList();
    }

    private static IEnumerable<NearDuplicateGroup> Cluster(List<MediaFile> items, MediaKind kind)
    {
        var n = items.Count;
        if (n < 2) yield break;

        // Pre-parse fingerprints once.
        var audio = kind == MediaKind.Audio
            ? items.Select(f => AudioFingerprinter.Parse(f.AudioFingerprint)).ToArray()
            : Array.Empty<uint[]>();
        var video = kind == MediaKind.Video
            ? items.Select(f => VideoFingerprinter.Parse(f.VideoFingerprint)).ToArray()
            : Array.Empty<ulong[]>();

        var threshold = kind == MediaKind.Audio ? AudioThreshold : VideoThreshold;
        var uf = new UnionFind(n);
        var pairSim = new Dictionary<(int, int), double>();

        for (var i = 0; i < n; i++)
        for (var j = i + 1; j < n; j++)
        {
            // Skip comparing files with wildly different durations (cheap prefilter).
            if (!DurationsComparable(items[i], items[j])) continue;

            var sim = kind == MediaKind.Audio
                ? AudioSimilarity(audio[i], audio[j])
                : VideoSimilarity(video[i], video[j]);

            if (sim >= threshold)
            {
                uf.Union(i, j);
                pairSim[(i, j)] = sim;
            }
        }

        foreach (var cluster in uf.Groups().Where(g => g.Count > 1))
        {
            // Group confidence = the weakest edge among members we actually compared.
            var sims = pairSim
                .Where(kv => cluster.Contains(kv.Key.Item1) && cluster.Contains(kv.Key.Item2))
                .Select(kv => kv.Value)
                .DefaultIfEmpty(threshold);
            yield return new NearDuplicateGroup
            {
                Kind = kind,
                Files = cluster.Select(idx => items[idx]).ToList(),
                MinSimilarity = sims.Min()
            };
        }
    }

    private static bool DurationsComparable(MediaFile a, MediaFile b)
    {
        if (a.DurationSeconds <= 0 || b.DurationSeconds <= 0) return true; // unknown → don't exclude
        var tolerance = Math.Max(3.0, Math.Max(a.DurationSeconds, b.DurationSeconds) * 0.05);
        return Math.Abs(a.DurationSeconds - b.DurationSeconds) <= tolerance;
    }

    /// <summary>Minimal union-find for clustering matched pairs into groups.</summary>
    private sealed class UnionFind
    {
        private readonly int[] _parent;
        public UnionFind(int n)
        {
            _parent = new int[n];
            for (var i = 0; i < n; i++) _parent[i] = i;
        }
        private int Find(int x) => _parent[x] == x ? x : _parent[x] = Find(_parent[x]);
        public void Union(int a, int b) => _parent[Find(a)] = Find(b);
        public IEnumerable<List<int>> Groups()
        {
            var map = new Dictionary<int, List<int>>();
            for (var i = 0; i < _parent.Length; i++)
                (map.TryGetValue(Find(i), out var l) ? l : map[Find(i)] = new List<int>()).Add(i);
            return map.Values;
        }
    }
}
