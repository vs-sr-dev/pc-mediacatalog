using MediaCatalog.Core.Classification;
using MediaCatalog.Core.Duplicates;
using MediaCatalog.Core.Models;
using MediaCatalog.Core.Storage;

namespace MediaCatalog.Core.Consolidation;

/// <param name="SameContent">
/// True when the two are byte-for-byte the same file, which settles the question without
/// asking anybody: the copy in the library is kept and the one that triggered the
/// consolidation is redundant.
/// </param>
/// <param name="Description">The episode both files claim to be, for the dialog's heading.</param>
public record EpisodeConflict(
    MediaFile Incoming,
    MediaFile Existing,
    IReadOnlyList<MediaFile> IncomingCopies,
    IReadOnlyList<MediaFile> ExistingCopies,
    bool SameContent,
    string Description);

/// <summary>
/// Finds the episode that is already in the library.
///
/// A name collision is not the only way to end up with the same episode twice. Two releases
/// of one episode carry two different names, so consolidating the second one lands it
/// beside the first with nothing at all to say they are the same episode — and the library
/// quietly gains a duplicate, which is precisely what a consolidation location exists to
/// prevent. What identifies an episode is the show, the season and the episode number, not
/// the name somebody gave the file, so that is what is looked for here.
/// </summary>
public static class LibraryEpisodes
{
    /// <summary>
    /// The file already filed in the library as this same episode, or null when there is
    /// none. Only ever the library's copy: a file sitting anywhere else is a duplicate to
    /// be dealt with in the ordinary way, not an obstacle to filing this one.
    /// </summary>
    public static MediaFile? FindSameEpisode(
        MediaFile file,
        string category,
        IReadOnlyList<MediaFile> catalogue,
        AppSettings settings,
        Func<MediaFile, string> categoryOf)
    {
        if (!Identifies(file, category)) return null;

        var title = TitleDuplicateFinder.Normalise(file.EffectiveTitle);
        if (title.Length == 0) return null;

        foreach (var other in catalogue)
        {
            if (ReferenceEquals(other, file)) continue;
            if (ConsolidationPlanner.PathsEqual(other.FullPath, file.FullPath)) continue;

            var otherCategory = categoryOf(other);
            if (!Identifies(other, otherCategory)) continue;
            if (!ConsolidationPlanner.IsUnderConsolidationRoot(other, settings)) continue;

            if (other.Season != file.Season || other.Episode != file.Episode) continue;
            if (Last(other) != Last(file)) continue;
            if (!string.Equals(TitleDuplicateFinder.Normalise(other.EffectiveTitle), title,
                    StringComparison.OrdinalIgnoreCase)) continue;

            if (!File.Exists(other.FullPath)) continue;
            return other;
        }

        return null;
    }

    /// <summary>
    /// True when the file says which episode it is — the only basis on which two
    /// differently named files can be called the same episode.
    /// </summary>
    private static bool Identifies(MediaFile file, string category) =>
        category is CategoryResolver.TvShow &&
        file is { Season: not null, Episode: not null };

    /// <summary>The last episode a file holds, so a double is not taken for a single.</summary>
    private static int? Last(MediaFile file) =>
        file.EpisodeEnd is { } end && end > file.Episode ? end : file.Episode;

    /// <summary>How to describe the episode two files are arguing over.</summary>
    public static string Describe(MediaFile file) =>
        $"{file.EffectiveTitle.Trim()} {file.NumberingDisplay}".Trim();
}
