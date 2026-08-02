using System.Globalization;
using System.Text.Json;
using MediaCatalog.Core.Models;
using MediaCatalog.Core.Tools;

namespace MediaCatalog.Core.Integrity;

/// <param name="Height">Picture height in pixels of the largest video stream; 0 if none.</param>
/// <param name="BitrateKbps">Overall bitrate in kbps, for audio quality; 0 if unknown.</param>
public record IntegrityCheckResult(
    IntegrityStatus Status, double DurationSeconds, string Detail,
    int Height = 0, int BitrateKbps = 0);

/// <summary>
/// Deep integrity checks backed by FFmpeg/ffprobe:
///  - <see cref="ProbeAsync"/> is fast: reads the container header for the duration, the
///    picture size and the bitrate — everything the Length and Quality columns show.
///  - <see cref="DeepDecodeAsync"/> fully decodes the file to catch truncation/corruption
///    that only shows up mid-stream (slow — use on demand, not during bulk scans).
/// </summary>
public class MediaIntegrityChecker
{
    private readonly ExternalTools _tools;

    public MediaIntegrityChecker(ExternalTools tools) => _tools = tools;

    /// <summary>
    /// Header probe: duration, picture height and bitrate in one pass. A file ffprobe
    /// can't parse is treated as corrupt.
    /// </summary>
    public async Task<IntegrityCheckResult> ProbeAsync(string path, CancellationToken ct = default)
    {
        if (!_tools.HasFfprobe)
            return new IntegrityCheckResult(IntegrityStatus.NotChecked, 0, "ffprobe not available.");

        // JSON rather than the flat key=value form: a file has many streams, and the flat
        // form gives no way to tell which value belongs to which of them.
        var args = $"-v error -print_format json -show_format -show_streams \"{path}\"";
        var result = await ProcessRunner.RunAsync(_tools.FfprobePath!, args, ct, timeoutMs: 60_000);

        if (result.TimedOut)
            return new IntegrityCheckResult(IntegrityStatus.NotChecked, 0, "Probe timed out.");
        if (result.ExitCode != 0)
            return new IntegrityCheckResult(IntegrityStatus.Corrupt, 0,
                Firstline(result.StdErr) ?? "ffprobe could not read the file.");

        var (seconds, height, bitrate) = ReadProbe(result.StdOut);

        // Readable container but no usable duration — suspicious.
        return seconds > 0
            ? new IntegrityCheckResult(IntegrityStatus.Ok, seconds, "OK", height, bitrate)
            : new IntegrityCheckResult(IntegrityStatus.Corrupt, 0, "No valid duration reported.",
                height, bitrate);
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

    /// <summary>
    /// Pull the three numbers worth keeping out of ffprobe's JSON. The tallest video
    /// stream wins the height — a cover-art thumbnail embedded in an audio file must not
    /// be mistaken for the picture — and the bitrate comes from the container, falling
    /// back to the first audio stream when the container does not state one.
    /// </summary>
    private static (double Seconds, int Height, int BitrateKbps) ReadProbe(string json)
    {
        double seconds = 0;
        var height = 0;
        long bitsPerSecond = 0;
        long audioBits = 0;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("format", out var format))
            {
                seconds = Number(format, "duration");
                bitsPerSecond = (long)Number(format, "bit_rate");
            }

            if (root.TryGetProperty("streams", out var streams) &&
                streams.ValueKind == JsonValueKind.Array)
            {
                foreach (var stream in streams.EnumerateArray())
                {
                    var type = stream.TryGetProperty("codec_type", out var t) ? t.GetString() : null;

                    if (string.Equals(type, "video", StringComparison.OrdinalIgnoreCase))
                    {
                        var h = (int)Number(stream, "height");
                        if (h > height) height = h;
                    }
                    else if (string.Equals(type, "audio", StringComparison.OrdinalIgnoreCase) &&
                             audioBits == 0)
                    {
                        audioBits = (long)Number(stream, "bit_rate");
                    }

                    // Some containers only state the duration per stream.
                    if (seconds <= 0) seconds = Number(stream, "duration");
                }
            }
        }
        catch (JsonException) { /* an unreadable probe is simply no information */ }

        if (bitsPerSecond <= 0) bitsPerSecond = audioBits;
        return (seconds, height, (int)Math.Round(bitsPerSecond / 1000.0));
    }

    /// <summary>
    /// A numeric property, whether ffprobe wrote it as a number or as a string (it does
    /// both, depending on the field). 0 when absent or "N/A".
    /// </summary>
    private static double Number(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return 0;
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String => double.TryParse(value.GetString(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var parsed) ? parsed : 0,
            _ => 0
        };
    }

    private static string? Firstline(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var idx = s.IndexOf('\n');
        return (idx < 0 ? s : s[..idx]).Trim();
    }
}
