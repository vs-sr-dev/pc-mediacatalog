using MediaCatalog.Core.Models;

namespace MediaCatalog.Core.Classification;

/// <summary>
/// Keeps what is known about a piece of content consistent across every copy of it.
/// Two files with the same content hash are the same episode however they are named, so
/// a season/episode or title worked out from one of them belongs on all of them.
/// </summary>
public static class DuplicateMetadata
{
    /// <summary>
    /// Share season/episode and confirmed titles within each set of identical files.
    /// Returns how many entries were changed.
    /// </summary>
    public static int Propagate(IEnumerable<MediaFile> files)
    {
        var changed = 0;

        foreach (var group in files.Where(f => f.HasHash)
                     .GroupBy(f => f.Sha256, StringComparer.OrdinalIgnoreCase))
        {
            var copies = group.ToList();
            if (copies.Count < 2) continue;

            // A hand-typed value is the most authoritative, then a TMDb one, then
            // whatever was parsed from a file name.
            var titleSource = copies.FirstOrDefault(f => f.TitleManuallySet)
                              ?? copies.FirstOrDefault(f => f.ImdbVerified)
                              ?? copies.FirstOrDefault(f => f.TmdbVerified);
            // Numbering somebody typed in beats numbering read out of a file name, for the
            // same reason a hand-typed title does.
            var numbering =
                copies.FirstOrDefault(f => f is { NumberingManuallySet: true, Season: not null, Episode: not null })
                ?? copies.FirstOrDefault(f => f is { Season: not null, Episode: not null });

            foreach (var copy in copies)
            {
                if (titleSource != null && !ReferenceEquals(copy, titleSource) &&
                    (!copy.TmdbVerified ||
                     !string.Equals(copy.TmdbName, titleSource.TmdbName, StringComparison.Ordinal)))
                {
                    copy.TmdbName = titleSource.TmdbName;
                    copy.TmdbVerified = titleSource.TmdbVerified;
                    copy.ImdbVerified = titleSource.ImdbVerified;
                    copy.TitleManuallySet = titleSource.TitleManuallySet;
                    changed++;
                }

                if (numbering != null && !ReferenceEquals(copy, numbering) &&
                    !(copy.NumberingManuallySet && !numbering.NumberingManuallySet) &&
                    (copy.Season != numbering.Season || copy.Episode != numbering.Episode ||
                     copy.EpisodeEnd != numbering.EpisodeEnd))
                {
                    copy.Season = numbering.Season;
                    copy.Episode = numbering.Episode;
                    copy.EpisodeEnd = numbering.EpisodeEnd;
                    copy.NumberingManuallySet = numbering.NumberingManuallySet;
                    changed++;
                }
            }
        }

        return changed;
    }

    /// <summary>
    /// Apply a folder's title rule to a file that has no title of its own. Only rules for
    /// folders that have not yet been migrated onto their files still reach this — see
    /// <see cref="Scanning.CatalogRefresher"/> — but a newly scanned file under such a
    /// folder still needs picking up.
    /// </summary>
    public static bool ApplyFolderTitle(MediaFile file, Storage.AppSettings settings)
    {
        // A title the user typed on the file itself, or one a source confirmed, is more
        // specific than a blanket rule on the folder and wins.
        if (file.TitleVerified) return false;

        var title = settings.TitleForPath(file.FullPath);
        if (string.IsNullOrWhiteSpace(title)) return false;
        if (string.Equals(file.TmdbName, title, StringComparison.Ordinal)) return false;

        // Recorded as the user's own choice, which is what a folder rule is — not as
        // something TMDb confirmed, which it never was.
        file.TmdbName = title.Trim();
        file.TitleManuallySet = true;
        return true;
    }
}
