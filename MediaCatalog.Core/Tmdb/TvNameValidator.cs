using System.Text.RegularExpressions;
using MediaCatalog.Core.Models;

namespace MediaCatalog.Core.Tmdb;

public record ValidationProgress(int Done, int Total, string Current);

/// <summary>
/// Validates TV show names against TMDb. Tries the filename-derived title first, then
/// walks the containing directories outward (e.g. …\Bewitched\Season 01\ep.avi tries the
/// episode title, then "Bewitched"), so mis-named files can still be identified by their
/// folder. Results are cached by <see cref="TmdbClient"/>.
/// </summary>
public class TvNameValidator
{
    private static readonly Regex SeasonFolder = new(
        @"^(season|series)\s*\d+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly TmdbClient _client;

    public TvNameValidator(TmdbClient client) => _client = client;

    /// <summary>Validate one file, setting <see cref="MediaFile.TmdbVerified"/>/Name on success.</summary>
    public async Task<bool> ValidateAsync(MediaFile file, CancellationToken ct = default)
    {
        if (file.TmdbVerified) return true;

        foreach (var candidate in Candidates(file))
        {
            var result = await _client.ValidateTvAsync(candidate, ct);
            if (result.Found)
            {
                file.TmdbVerified = true;
                file.TmdbName = result.CanonicalName;
                return true;
            }
        }
        return false;
    }

    /// <summary>Validate every not-yet-verified TV file, reporting progress.</summary>
    public async Task<int> ValidateManyAsync(
        IEnumerable<MediaFile> files,
        IProgress<ValidationProgress>? progress = null,
        CancellationToken ct = default)
    {
        var targets = files
            .Where(f => f.Kind == MediaKind.Video &&
                        f.VideoCategory == VideoCategory.TvShow &&
                        !f.TmdbVerified)
            .ToList();

        var validated = 0;
        for (var i = 0; i < targets.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var file = targets[i];
            progress?.Report(new ValidationProgress(i, targets.Count, file.ParsedTitle));
            if (await ValidateAsync(file, ct)) validated++;
        }
        progress?.Report(new ValidationProgress(targets.Count, targets.Count, string.Empty));
        return validated;
    }

    /// <summary>
    /// Names to try, in order: the parsed episode title, then each ancestor directory
    /// name (skipping season folders, single-letter buckets and the drive root).
    /// </summary>
    public static IEnumerable<string> Candidates(MediaFile file)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string? Clean(string s)
        {
            var t = s.Replace('.', ' ').Replace('_', ' ').Trim();
            return string.IsNullOrWhiteSpace(t) ? null : t;
        }

        if (Clean(file.ParsedTitle) is { } title && seen.Add(title))
            yield return title;

        var dir = Path.GetDirectoryName(file.FullPath);
        while (!string.IsNullOrEmpty(dir))
        {
            var name = Path.GetFileName(dir);
            dir = Path.GetDirectoryName(dir);

            if (string.IsNullOrEmpty(name)) continue;   // drive root (e.g. "X:\")
            if (name.Length <= 1) continue;             // single-letter bucket
            if (SeasonFolder.IsMatch(name)) continue;   // "Season 01"

            if (Clean(name) is { } candidate && seen.Add(candidate))
                yield return candidate;
        }
    }
}
