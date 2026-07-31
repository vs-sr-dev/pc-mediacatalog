using System.Text.RegularExpressions;
using MediaCatalog.Core.Models;
using MediaCatalog.Core.Naming;

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

    /// <summary>
    /// Validate every not-yet-verified TV file, reporting progress. A confirmed name is
    /// shared with the other files that had the same title, which both fixes whole shows
    /// in one lookup and spares the remaining episodes a query each.
    /// </summary>
    public async Task<int> ValidateManyAsync(
        IEnumerable<MediaFile> files,
        IProgress<ValidationProgress>? progress = null,
        CancellationToken ct = default)
    {
        var all = files as IList<MediaFile> ?? files.ToList();
        var targets = all
            .Where(f => f.Kind == MediaKind.Video &&
                        f.VideoCategory is VideoCategory.TvShow or VideoCategory.TvExtra &&
                        !f.TmdbVerified)
            .ToList();

        var validated = 0;
        for (var i = 0; i < targets.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var file = targets[i];
            progress?.Report(new ValidationProgress(i, targets.Count, file.ParsedTitle));

            var previousTitle = file.EffectiveTitle;
            if (!await ValidateAsync(file, ct)) continue;

            validated++;
            validated += TitleUpdater.Propagate(all, file, previousTitle, manual: false,
                scope: f => f.Kind == MediaKind.Video &&
                            f.VideoCategory is VideoCategory.TvShow or VideoCategory.TvExtra);
        }
        progress?.Report(new ValidationProgress(targets.Count, targets.Count, string.Empty));
        return validated;
    }

    // Trailing "(2004)" / "[1080p]" and similar decoration on a folder name.
    private static readonly Regex TrailingBracket = new(
        @"[\(\[\{][^\)\]\}]*[\)\]\}]\s*$", RegexOptions.Compiled);

    /// <summary>
    /// Names to try, in order: the parsed episode title, then **every** ancestor directory
    /// name up to the drive root. Season folders and single-letter buckets are tried last
    /// rather than skipped, so a show that really is called "Ed" or "Season 9" can still be
    /// found. Each name is also offered with trailing decoration — "(2004)", "[1080p]" —
    /// stripped, since folders are commonly annotated that way.
    /// </summary>
    public static IEnumerable<string> Candidates(MediaFile file)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deferred = new List<string>();

        static string? Clean(string s)
        {
            var t = s.Replace('.', ' ').Replace('_', ' ').Trim();
            return string.IsNullOrWhiteSpace(t) ? null : t;
        }

        IEnumerable<string> Variants(string raw)
        {
            if (Clean(raw) is not { } cleaned) yield break;
            if (seen.Add(cleaned)) yield return cleaned;

            var stripped = TrailingBracket.Replace(cleaned, "").Trim();
            if (stripped.Length > 0 && seen.Add(stripped)) yield return stripped;
        }

        foreach (var candidate in Variants(file.ParsedTitle)) yield return candidate;

        var dir = Path.GetDirectoryName(file.FullPath);
        while (!string.IsNullOrEmpty(dir))
        {
            var name = Path.GetFileName(dir);
            dir = Path.GetDirectoryName(dir);
            if (string.IsNullOrEmpty(name)) continue;   // drive root (e.g. "X:\")

            // Unlikely names are held back rather than dropped: if nothing else matches,
            // they are still better than giving up.
            if (name.Length <= 1 || SeasonFolder.IsMatch(name))
            {
                deferred.Add(name);
                continue;
            }

            foreach (var candidate in Variants(name)) yield return candidate;
        }

        foreach (var name in deferred)
            foreach (var candidate in Variants(name))
                yield return candidate;
    }
}
