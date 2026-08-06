using MediaCatalog.Core.Models;

namespace MediaCatalog.Core.Classification;

/// <summary>
/// Attaches each extra (special/featurette) to the film or episode it belongs to, so the
/// two stay together: the extra adopts the owner's title, year and TV/film flavour, and
/// records the owner's id in <see cref="MediaFile.LinkedFileId"/> so relocation and
/// consolidation can move them as a unit.
///
/// The owner is the nearest main media file at or above the extra's folder — in
/// <c>…\Yes Minister\Extras\bts.mkv</c> the episodes under <c>…\Yes Minister\</c> own it.
/// </summary>
public static class ExtraLinker
{
    /// <summary>How far above a file we look for (or index) an owner.</summary>
    private const int MaxLevels = 6;

    /// <summary>Link every extra to its owner. Returns how many were linked.</summary>
    public static int Link(IEnumerable<MediaFile> files)
    {
        var all = files as IList<MediaFile> ?? files.ToList();

        var mains = all.Where(f => f.Kind == MediaKind.Video && !CountsAsExtra(f) &&
                                   (f.VideoCategory is VideoCategory.TvShow or VideoCategory.Movie ||
                                    ClaimedAsMain(f)))
                       .ToList();
        var extras = all.Where(CountsAsExtra).ToList();
        if (extras.Count == 0) return 0;

        // Index the main files under each of their ancestor directories, so an extra can
        // find "the show this folder is about" without rescanning the whole catalogue.
        var index = new Dictionary<string, List<MediaFile>>(StringComparer.OrdinalIgnoreCase);
        foreach (var main in mains)
            foreach (var dir in Ancestors(main.FullPath))
                if (index.TryGetValue(dir, out var list)) list.Add(main);
                else index[dir] = new List<MediaFile> { main };

        var linked = 0;
        foreach (var extra in extras)
        {
            var owner = FindOwner(extra, index);
            if (owner == null)
            {
                extra.LinkedFileId = string.Empty;
                continue;
            }

            Adopt(extra, owner);
            linked++;
        }
        return linked;
    }

    /// <summary>The main file an extra belongs to, or null if nothing plausible is near.</summary>
    public static MediaFile? FindOwner(MediaFile extra, Dictionary<string, List<MediaFile>> index)
    {
        foreach (var dir in Ancestors(extra.FullPath))
        {
            if (!index.TryGetValue(dir, out var candidates) || candidates.Count == 0) continue;

            // An extra filed under a season folder belongs to that season; otherwise the
            // main feature (the largest file) is the best representative of the title.
            var scoped = extra.Season is { } s && extra.Season != 0
                ? candidates.Where(c => c.Season == s).ToList()
                : candidates;
            if (scoped.Count == 0) scoped = candidates;

            return scoped.OrderByDescending(c => c.SizeBytes).First();
        }
        return null;
    }

    /// <summary>
    /// True when a file is bonus material as far as linking is concerned.
    ///
    /// A category the user set by hand is the last word on what a file is. Somebody who has
    /// told the catalogue that a file filed as a featurette is in fact an episode is not
    /// asking to have it quietly re-adopted as a featurette every time anything is linked —
    /// which is what used to happen, and what made a title typed onto such a file vanish
    /// again before the rename that was to follow it could see it.
    /// </summary>
    public static bool CountsAsExtra(MediaFile file) =>
        file.IsExtra && !ClaimedAsMain(file);

    /// <summary>True when the user's own category says this is a programme or a film.</summary>
    private static bool ClaimedAsMain(MediaFile file) =>
        string.Equals(file.CategoryOverride, CategoryResolver.TvShow, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(file.CategoryOverride, CategoryResolver.Movie, StringComparison.OrdinalIgnoreCase);

    /// <summary>Copy the owner's identity onto the extra so both file to the same place.</summary>
    private static void Adopt(MediaFile extra, MediaFile owner)
    {
        extra.LinkedFileId = owner.Id;
        extra.VideoCategory = owner.VideoCategory == VideoCategory.TvShow
            ? VideoCategory.TvExtra
            : VideoCategory.MovieExtra;

        // A title the user typed is theirs, on an extra as much as on anything else. The
        // owner's title is a sensible default for a featurette that has never been named;
        // it is not a correction to be applied over somebody's own words.
        if (!extra.TitleManuallySet)
        {
            if (!string.IsNullOrWhiteSpace(owner.ParsedTitle)) extra.ParsedTitle = owner.ParsedTitle;
            if (!string.IsNullOrWhiteSpace(owner.TmdbName))
            {
                extra.TmdbName = owner.TmdbName;
                extra.TmdbVerified = owner.TmdbVerified;
                extra.ImdbVerified = owner.ImdbVerified;
                extra.TitleManuallySet = owner.TitleManuallySet;
            }
        }
        extra.Year ??= owner.Year;
    }

    /// <summary>The file's own directory and its parents, nearest first (capped).</summary>
    private static IEnumerable<string> Ancestors(string fullPath)
    {
        var dir = Path.GetDirectoryName(fullPath);
        for (var i = 0; i < MaxLevels && !string.IsNullOrEmpty(dir); i++)
        {
            yield return dir;
            dir = Path.GetDirectoryName(dir);
        }
    }
}
