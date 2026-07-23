using MediaCatalog.Core.Tools;

namespace MediaCatalog.Core.Fingerprinting;

/// <summary>
/// Computes a Chromaprint acoustic fingerprint via <c>fpcalc</c>. The fingerprint
/// recognises the same recording across different encodings/bitrates, which plain
/// file hashing cannot.
/// </summary>
public class AudioFingerprinter
{
    private readonly ExternalTools _tools;

    public AudioFingerprinter(ExternalTools tools) => _tools = tools;

    public bool IsAvailable => _tools.CanDoAudio;

    /// <summary>
    /// Returns the raw fingerprint as a comma-separated list of uint32 values, or
    /// empty on failure. Uses the first 120s, enough to identify a track reliably.
    /// </summary>
    public async Task<string> ComputeAsync(string path, CancellationToken ct = default)
    {
        if (!_tools.HasFpcalc) return string.Empty;

        var args = $"-raw -length 120 \"{path}\"";
        var result = await ProcessRunner.RunAsync(_tools.FpcalcPath!, args, ct, timeoutMs: 60_000);
        if (result.ExitCode != 0) return string.Empty;

        // fpcalc prints "DURATION=...\nFINGERPRINT=1,2,3,..."
        foreach (var line in result.StdOut.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("FINGERPRINT=", StringComparison.OrdinalIgnoreCase))
                return trimmed["FINGERPRINT=".Length..].Trim();
        }
        return string.Empty;
    }

    /// <summary>Parse a stored fingerprint string back into its uint32 array.</summary>
    public static uint[] Parse(string fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint)) return Array.Empty<uint>();
        var parts = fingerprint.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var values = new uint[parts.Length];
        var n = 0;
        foreach (var p in parts)
            if (uint.TryParse(p.Trim(), out var v))
                values[n++] = v;
        return n == values.Length ? values : values[..n];
    }
}
