using System.Globalization;
using MediaCatalog.Core.Models;
using MediaCatalog.Core.Tools;

namespace MediaCatalog.Core.Integrity;

public record IntegrityCheckResult(IntegrityStatus Status, double DurationSeconds, string Detail);

/// <summary>
/// Deep integrity checks backed by FFmpeg/ffprobe:
///  - <see cref="ProbeAsync"/> is fast: reads the container header and duration.
///  - <see cref="DeepDecodeAsync"/> fully decodes the file to catch truncation/corruption
///    that only shows up mid-stream (slow — use on demand, not during bulk scans).
/// </summary>
public class MediaIntegrityChecker
{
    private readonly ExternalTools _tools;

    public MediaIntegrityChecker(ExternalTools tools) => _tools = tools;

    /// <summary>Header/duration probe. A file ffprobe can't parse is treated as corrupt.</summary>
    public async Task<IntegrityCheckResult> ProbeAsync(string path, CancellationToken ct = default)
    {
        if (!_tools.HasFfprobe)
            return new IntegrityCheckResult(IntegrityStatus.NotChecked, 0, "ffprobe not available.");

        var args =
            $"-v error -show_entries format=duration " +
            $"-of default=noprint_wrappers=1:nokey=1 \"{path}\"";
        var result = await ProcessRunner.RunAsync(_tools.FfprobePath!, args, ct, timeoutMs: 30_000);

        if (result.ExitCode != 0)
            return new IntegrityCheckResult(IntegrityStatus.Corrupt, 0,
                Firstline(result.StdErr) ?? "ffprobe could not read the file.");

        var text = result.StdOut.Trim();
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            && seconds > 0)
            return new IntegrityCheckResult(IntegrityStatus.Ok, seconds, "OK");

        // Readable container but no usable duration — suspicious.
        return new IntegrityCheckResult(IntegrityStatus.Corrupt, 0, "No valid duration reported.");
    }

    /// <summary>Full decode pass. Any decode error marks the file corrupt.</summary>
    public async Task<IntegrityCheckResult> DeepDecodeAsync(string path, CancellationToken ct = default)
    {
        if (!_tools.HasFfmpeg)
            return new IntegrityCheckResult(IntegrityStatus.NotChecked, 0, "ffmpeg not available.");

        // Decode everything, discard output; -xerror aborts on the first error.
        var args = $"-v error -xerror -i \"{path}\" -f null -";
        var result = await ProcessRunner.RunAsync(_tools.FfmpegPath!, args, ct, timeoutMs: 600_000);

        if (result.TimedOut)
            return new IntegrityCheckResult(IntegrityStatus.NotChecked, 0, "Decode timed out.");

        var errors = result.StdErr.Trim();
        if (result.ExitCode == 0 && errors.Length == 0)
            return new IntegrityCheckResult(IntegrityStatus.Ok, 0, "Decoded cleanly.");

        return new IntegrityCheckResult(IntegrityStatus.Corrupt, 0,
            Firstline(errors) ?? "Decode reported errors.");
    }

    private static string? Firstline(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var idx = s.IndexOf('\n');
        return (idx < 0 ? s : s[..idx]).Trim();
    }
}
