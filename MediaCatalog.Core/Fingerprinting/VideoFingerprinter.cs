using System.Globalization;
using MediaCatalog.Core.Tools;

namespace MediaCatalog.Core.Fingerprinting;

/// <summary>
/// Builds a perceptual video signature: sample frames spread across the whole file,
/// reduce each to a tiny grayscale image and compute a 64-bit dHash. Two encodings of
/// the same film produce near-identical signatures even when their file hashes differ.
///
/// This is inherently fuzzy — matches are "candidates", not certainties.
/// </summary>
public class VideoFingerprinter
{
    private const int FrameCount = 16;   // samples across the video
    private const int W = 9;             // dHash needs width = hashWidth + 1
    private const int H = 8;
    private const int FrameBytes = W * H;

    private readonly ExternalTools _tools;

    public VideoFingerprinter(ExternalTools tools) => _tools = tools;

    public bool IsAvailable => _tools.CanDoVideo;

    /// <summary>
    /// Returns the signature as hex (16 hex chars per sampled frame), or empty on
    /// failure. <paramref name="durationSeconds"/> spreads the samples across the file.
    /// </summary>
    public async Task<string> ComputeAsync(string path, double durationSeconds, CancellationToken ct = default)
    {
        if (!_tools.HasFfmpeg) return string.Empty;

        // Choose a sampling rate that yields ~FrameCount frames over the whole file.
        // For very short/zero-duration files, fall back to grabbing the first frames.
        string vf;
        if (durationSeconds > 1)
        {
            var fps = (FrameCount / durationSeconds).ToString("0.######", CultureInfo.InvariantCulture);
            vf = $"fps={fps},scale={W}:{H},format=gray";
        }
        else
        {
            vf = $"scale={W}:{H},format=gray";
        }

        var args = $"-v error -i \"{path}\" -vf \"{vf}\" -frames:v {FrameCount} -f rawvideo -";
        var (exit, bytes, _) = await ProcessRunner.RunBinaryAsync(_tools.FfmpegPath!, args, ct, timeoutMs: 120_000);
        if (exit != 0 && bytes.Length < FrameBytes) return string.Empty;

        var frameCount = bytes.Length / FrameBytes;
        if (frameCount == 0) return string.Empty;

        var hashes = new ulong[frameCount];
        for (var f = 0; f < frameCount; f++)
            hashes[f] = DHash(bytes, f * FrameBytes);

        return string.Concat(hashes.Select(h => h.ToString("x16")));
    }

    /// <summary>Row-wise difference hash over a 9x8 grayscale frame → 64 bits.</summary>
    private static ulong DHash(byte[] data, int offset)
    {
        ulong hash = 0;
        var bit = 0;
        for (var row = 0; row < H; row++)
        {
            var rowStart = offset + row * W;
            for (var col = 0; col < W - 1; col++)
            {
                if (data[rowStart + col] < data[rowStart + col + 1])
                    hash |= 1UL << bit;
                bit++;
            }
        }
        return hash;
    }

    /// <summary>Parse a stored hex signature back into per-frame 64-bit hashes.</summary>
    public static ulong[] Parse(string signature)
    {
        if (string.IsNullOrWhiteSpace(signature) || signature.Length % 16 != 0)
            return Array.Empty<ulong>();
        var count = signature.Length / 16;
        var hashes = new ulong[count];
        for (var i = 0; i < count; i++)
            hashes[i] = ulong.Parse(signature.AsSpan(i * 16, 16), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return hashes;
    }
}
