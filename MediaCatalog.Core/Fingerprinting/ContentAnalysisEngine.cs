using MediaCatalog.Core.Integrity;
using MediaCatalog.Core.Models;
using MediaCatalog.Core.Tools;

namespace MediaCatalog.Core.Fingerprinting;

public record AnalysisProgress(int Done, int Total, string CurrentFile);

/// <summary>
/// Batch content analysis backed by the external tools: probe duration/integrity,
/// compute perceptual fingerprints, and (optionally) run a deep decode check.
/// Skips work already done so it can be re-run cheaply.
/// </summary>
public class ContentAnalysisEngine
{
    private readonly ExternalTools _tools;
    private readonly MediaIntegrityChecker _integrity;
    private readonly AudioFingerprinter _audio;
    private readonly VideoFingerprinter _video;

    public ContentAnalysisEngine(ExternalTools tools)
    {
        _tools = tools;
        _integrity = new MediaIntegrityChecker(tools);
        _audio = new AudioFingerprinter(tools);
        _video = new VideoFingerprinter(tools);
    }

    public async Task AnalyzeAsync(
        IReadOnlyList<MediaFile> files,
        bool fingerprint,
        bool deepCheck,
        IProgress<AnalysisProgress>? progress = null,
        CancellationToken ct = default)
    {
        var total = files.Count;
        for (var i = 0; i < total; i++)
        {
            ct.ThrowIfCancellationRequested();
            var file = files[i];
            progress?.Report(new AnalysisProgress(i, total, file.FileName));

            if (!File.Exists(file.FullPath)) continue;

            // 1. Probe for duration + basic integrity (needed for fingerprint bucketing).
            if (_tools.HasFfprobe && (file.DurationSeconds <= 0 || deepCheck))
            {
                var probe = await _integrity.ProbeAsync(file.FullPath, ct);
                if (probe.DurationSeconds > 0) file.DurationSeconds = probe.DurationSeconds;
                if (probe.Status != IntegrityStatus.NotChecked) file.Integrity = probe.Status;
            }

            // 2. Fingerprints (skip if already present and file unchanged).
            if (fingerprint && file.Integrity != IntegrityStatus.Corrupt)
            {
                if (file.Kind == MediaKind.Audio && _audio.IsAvailable &&
                    string.IsNullOrEmpty(file.AudioFingerprint))
                {
                    file.AudioFingerprint = await _audio.ComputeAsync(file.FullPath, ct);
                }
                else if (file.Kind == MediaKind.Video && _video.IsAvailable &&
                         string.IsNullOrEmpty(file.VideoFingerprint))
                {
                    file.VideoFingerprint =
                        await _video.ComputeAsync(file.FullPath, file.DurationSeconds, ct);
                }
            }

            // 3. Optional deep decode (authoritative but slow).
            if (deepCheck && _tools.HasFfmpeg)
            {
                var deep = await _integrity.DeepDecodeAsync(file.FullPath, ct);
                if (deep.Status != IntegrityStatus.NotChecked) file.Integrity = deep.Status;
            }
        }

        progress?.Report(new AnalysisProgress(total, total, string.Empty));
    }
}
