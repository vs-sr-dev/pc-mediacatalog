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

    /// <summary>Shift budget used while clustering the whole catalogue — about two seconds.</summary>
    private const int ClusterAudioShift = 16;
    private const int ClusterVideoShift = 3;

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

    // --- Comparing copies that do not start in the same place ----------------
    //
    // Two rips of one film rarely begin on the same frame. A second of distributor logo, a
    // black frame trimmed, an extra beat before the audio comes in — and from then on the
    // whole fingerprint is offset, so comparing them position by position says they are
    // nothing like each other when in fact they are the same thing a moment apart.
    //
    // So the comparison slides one against the other and keeps the best alignment it finds.
    // The straight comparison is tried first and, when it already agrees, nothing is slid at
    // all: the cost is only paid where there is a disagreement worth explaining.

    /// <summary>Chromaprint frames to slide by. Each is about an eighth of a second.</summary>
    public const int AudioShiftFrames = 40;

    /// <summary>Keyframe hashes to slide by, for video.</summary>
    public const int VideoShiftFrames = 8;

    /// <summary>The best chromaprint similarity over every alignment within the shift budget.</summary>
    public static double BestAudioSimilarity(uint[] a, uint[] b, int maxShift = AudioShiftFrames) =>
        BestOverShifts(a.Length, b.Length, maxShift, AudioThreshold,
            (i, j, n) => AudioSimilarity(a.AsSpan(i, n), b.AsSpan(j, n)));

    /// <summary>The same for perceptual video signatures.</summary>
    public static double BestVideoSimilarity(ulong[] a, ulong[] b, int maxShift = VideoShiftFrames) =>
        BestOverShifts(a.Length, b.Length, maxShift, VideoThreshold,
            (i, j, n) => VideoSimilarity(a.AsSpan(i, n), b.AsSpan(j, n)));

    private static double AudioSimilarity(ReadOnlySpan<uint> a, ReadOnlySpan<uint> b)
    {
        var n = Math.Min(a.Length, b.Length);
        if (n < 10) return 0;
        long diffBits = 0;
        for (var i = 0; i < n; i++) diffBits += BitOperations.PopCount(a[i] ^ b[i]);
        return 1.0 - diffBits / (double)(n * 32);
    }

    private static double VideoSimilarity(ReadOnlySpan<ulong> a, ReadOnlySpan<ulong> b)
    {
        var n = Math.Min(a.Length, b.Length);
        if (n == 0) return 0;
        long diffBits = 0;
        for (var i = 0; i < n; i++) diffBits += BitOperations.PopCount(a[i] ^ b[i]);
        return 1.0 - diffBits / (double)(n * 64);
    }

    /// <summary>
    /// Slide the two sequences past each other and keep the best score. Stops as soon as the
    /// alignment is good enough to have answered the question — which, for the two files that
    /// really did start at the same moment, is on the very first try.
    /// </summary>
    private static double BestOverShifts(
        int lengthA, int lengthB, int maxShift, double goodEnough,
        Func<int, int, int, double> score)
    {
        if (lengthA == 0 || lengthB == 0) return 0;

        var best = score(0, 0, Math.Min(lengthA, lengthB));
        if (best >= goodEnough) return best;

        var limit = Math.Min(maxShift, Math.Max(0, Math.Min(lengthA, lengthB) - 1));
        for (var shift = 1; shift <= limit; shift++)
        {
            // b starts later than a, then a starts later than b.
            var overlap = Math.Min(lengthA - shift, lengthB);
            if (overlap > 0) best = Math.Max(best, score(shift, 0, overlap));

            overlap = Math.Min(lengthA, lengthB - shift);
            if (overlap > 0) best = Math.Max(best, score(0, shift, overlap));

            if (best >= goodEnough) break;
        }
        return best;
    }

    /// <summary>
    /// How alike two catalogued files' fingerprints are, allowing for one starting a moment
    /// before the other. 0 when they cannot be compared at all — different kinds, or one of
    /// them has never been fingerprinted.
    /// </summary>
    public static double Similarity(MediaFile a, MediaFile b)
    {
        if (a.Kind != b.Kind) return 0;

        if (a.Kind == MediaKind.Audio)
        {
            if (string.IsNullOrEmpty(a.AudioFingerprint) || string.IsNullOrEmpty(b.AudioFingerprint))
                return 0;
            return BestAudioSimilarity(
                AudioFingerprinter.Parse(a.AudioFingerprint),
                AudioFingerprinter.Parse(b.AudioFingerprint));
        }

        if (a.Kind == MediaKind.Video)
        {
            if (string.IsNullOrEmpty(a.VideoFingerprint) || string.IsNullOrEmpty(b.VideoFingerprint))
                return 0;
            return BestVideoSimilarity(
                VideoFingerprinter.Parse(a.VideoFingerprint),
                VideoFingerprinter.Parse(b.VideoFingerprint));
        }

        return 0;
    }

    /// <summary>The threshold above which two files of this kind count as the same content.</summary>
    public static double ThresholdFor(MediaKind kind) =>
        kind == MediaKind.Audio ? AudioThreshold : VideoThreshold;

    /// <summary>
    /// True when the fingerprints say these two are the same content. False also covers
    /// "cannot tell" — a file with no fingerprint is not evidence of anything, and the
    /// caller is expected to treat that as a question for the user rather than an answer.
    /// </summary>
    public static bool LooksLikeSameContent(MediaFile a, MediaFile b) =>
        Similarity(a, b) >= ThresholdFor(a.Kind);

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

            // A modest shift budget here, and the full one only where the answer is being
            // acted on: this loop is every pair against every other pair, and a copy that
            // starts more than a couple of seconds out is rare enough to be worth finding
            // deliberately rather than paying for on every comparison in the library.
            var sim = kind == MediaKind.Audio
                ? BestAudioSimilarity(audio[i], audio[j], ClusterAudioShift)
                : BestVideoSimilarity(video[i], video[j], ClusterVideoShift);

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
